using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class FileDeletionStore(BlogDbContext db, IMediaWriteStore writes) : IFileDeletionStore
{
    public async Task DeleteAsync(Guid actorId, Guid fileId, CancellationToken ct)
    {
        var known = await db.MediaFiles.AsNoTracking().Where(f => f.Id == fileId && f.Purpose != MediaPurpose.Avatar)
            .Select(f => new { f.OwnerId }).SingleOrDefaultAsync(ct) ?? throw new DomainError("resource_not_found");
        await writes.ExecuteAsync(new(known.OwnerId, RoleActorId: actorId, FileIds: [fileId], DemandRole: false), async (session, token) =>
        {
            if (actorId != known.OwnerId && !session.RoleActor!.HasRole(LocalRole.Admin)) { throw new DomainError("resource_not_found"); }
            var file = await db.MediaFiles.SingleAsync(f => f.Id == fileId, token);
            await db.Entry(file).ReloadAsync(token);
            if (file.Purpose == MediaPurpose.Avatar || file.State == MediaFileState.Pending) { throw new DomainError("resource_not_found"); }
            if (file.State == MediaFileState.Deleted) { return 0; }
            if (await db.PublicationUsages.AnyAsync(u => u.FileId == fileId, token)) { throw new DomainError("file_in_use"); }
            file.Delete(session.UtcNow);
            await db.SaveChangesAsync(token);
            await db.MediaRepresentations.Where(r => r.FileId == fileId).ExecuteDeleteAsync(token);
            await session.ApplyUsageAsync(-file.OriginalSize, token);
            var ids = await db.MediaObjects.Where(o => o.FileId == fileId && (o.State == MediaObjectState.Present || o.State == MediaObjectState.Planned))
                .Select(o => o.Id).ToArrayAsync(token);
            foreach (var id in ids) { await session.RetireAsync(id, session.UtcNow, token); }
            return 0;
        }, ct);
    }
}
