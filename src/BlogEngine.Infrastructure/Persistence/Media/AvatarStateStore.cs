using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class AvatarStateStore(BlogDbContext db, IMediaWriteStore writes) : IAvatarStateStore
{
    public async Task<AvatarProfile> ProfileAsync(Guid ownerId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO media.avatar_states(user_id,version,updated_at) VALUES ({ownerId},{Guid.NewGuid()},clock_timestamp()) ON CONFLICT DO NOTHING", ct);
        // State, version and current roles are projected by one statement after the bootstrap winner commits.
        var profile = await db.AvatarStates.AsNoTracking().Where(a => a.UserId == ownerId).Select(a => new
        {
            a.CurrentFileId,
            a.Version,
            Roles = db.RoleAssignments.Where(r => r.UserId == ownerId).Select(r => r.Role).ToArray()
        }).SingleAsync(ct);
        return new(new CurrentActor(ownerId, profile.Roles), new(profile.CurrentFileId, profile.Version));
    }

    public Task<AvatarSnapshot> DeleteAsync(Guid ownerId, Guid version, CancellationToken ct) =>
        writes.ExecuteAsync(new(ownerId, Avatar: true), async (session, token) =>
        {
            var state = await db.AvatarStates.SingleAsync(a => a.UserId == ownerId, token);
            await db.Entry(state).ReloadAsync(token);
            var previous = state.CurrentFileId;
            state.Replace(version, null, session.UtcNow);
            await db.SaveChangesAsync(token);
            if (previous is { } id) { await RetireFileAsync(db, session, id, token); }
            return new AvatarSnapshot(state.CurrentFileId, state.Version);
        }, ct);

    internal static async Task RetireFileAsync(BlogDbContext db, IMediaWriteSession session, Guid id, CancellationToken ct)
    {
        var file = await db.MediaFiles.SingleAsync(f => f.Id == id, ct);
        await db.Entry(file).ReloadAsync(ct);
        file.Delete(session.UtcNow);
        await db.SaveChangesAsync(ct);
        await db.MediaRepresentations.Where(r => r.FileId == id).ExecuteDeleteAsync(ct);
        await session.ApplyUsageAsync(-file.OriginalSize, ct);
        var objects = await db.MediaObjects.Where(o => o.FileId == id && (o.State == MediaObjectState.Planned || o.State == MediaObjectState.Present))
            .Select(o => o.Id).ToArrayAsync(ct);
        foreach (var objectId in objects) { await session.RetireAsync(objectId, session.UtcNow, ct); }
    }
}
