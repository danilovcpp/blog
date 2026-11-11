using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class UploadLifecycleStore(BlogDbContext db, DatabaseClock clock, IMediaWriteStore writes) : IUploadLifecycleStore
{
    public async Task<UploadSnapshot> InitializeAsync(MediaFile candidate, MediaUpload request, CancellationToken ct)
    {
        var prior = await FindKeyAsync(request.OwnerId, request.IdempotencyKey, ct);
        if (prior is not null) { return await ReplayAsync(prior, request, ct); }
        return await writes.ExecuteAsync(new(request.OwnerId, RoleActorId: request.OwnerId, DemandRole: false), async (session, token) =>
        {
            prior = await FindKeyAsync(request.OwnerId, request.IdempotencyKey, token);
            if (prior is not null) { return await ReplayAsync(prior, request, token); }
            AccessPolicy.Demand(session.RoleActor!, ActorAction.CreatePublication);
            var file = MediaFile.Create(candidate.OwnerId, candidate.Purpose, candidate.OriginalName, candidate.DeclaredContentType, candidate.OriginalSize, session.UtcNow);
            var upload = MediaUpload.Create(file, request.IdempotencyKey, request.ExpectedAvatarVersion, session.UtcNow);
            await session.ReserveAsync(file, upload, token);
            return UploadSnapshot.From(upload, file, session.UtcNow);
        }, ct);
    }

    private Task<MediaUpload?> FindKeyAsync(Guid owner, Guid key, CancellationToken ct) =>
        db.MediaUploads.AsNoTracking().SingleOrDefaultAsync(u => u.OwnerId == owner && u.IdempotencyKey == key, ct);

    private async Task<UploadSnapshot> ReplayAsync(MediaUpload existing, MediaUpload request, CancellationToken ct)
    {
        if (!existing.RequestFingerprint.AsSpan().SequenceEqual(request.RequestFingerprint)) { throw new DomainError("idempotency_conflict"); }
        var file = await db.MediaFiles.AsNoTracking().SingleAsync(f => f.Id == existing.FileId, ct);
        return UploadSnapshot.From(existing, file, await clock.ReadAsync(db, ct));
    }

    public async Task<UploadSnapshot> GetAsync(Guid ownerId, Guid uploadId, CancellationToken ct)
    {
        var row = await (from u in db.MediaUploads.AsNoTracking()
                         join f in db.MediaFiles.AsNoTracking() on u.FileId equals f.Id
                         where u.Id == uploadId && u.OwnerId == ownerId
                         select new { Upload = u, File = f }).SingleOrDefaultAsync(ct) ?? throw new DomainError("resource_not_found");
        return UploadSnapshot.From(row.Upload, row.File, await clock.ReadAsync(db, ct));
    }

    public async Task CancelAsync(Guid ownerId, Guid uploadId, CancellationToken ct)
    {
        var known = await MediaUploadTransitions.FindOwnedAsync(db, ownerId, uploadId, ct);
        await writes.ExecuteAsync(new(ownerId, FileIds: [known.FileId]), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(uploadId, token) ?? throw new DomainError("resource_not_found");
            if (upload.State == MediaUploadState.Completed) { throw new DomainError("invalid_state"); }
            if (upload.IsActive)
            {
                var expired = upload.ExpiresAt <= session.UtcNow;
                await MediaUploadTransitions.TerminateAsync(db, session, upload,
                    expired ? MediaUploadState.Expired : MediaUploadState.Cancelled, expired ? "upload_expired" : "cancelled", token);
            }
            return 0;
        }, ct);
    }
}

internal static class MediaUploadTransitions
{
    public static async Task<MediaUpload> FindOwnedAsync(BlogDbContext db, Guid owner, Guid id, CancellationToken ct) =>
        await db.MediaUploads.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.OwnerId == owner, ct) ?? throw new DomainError("resource_not_found");

    public static async Task TerminateAsync(BlogDbContext db, IMediaWriteSession session, MediaUpload upload, MediaUploadState state, string code, CancellationToken ct)
    {
        var notBefore = upload.LeaseUntil > session.UtcNow ? upload.LeaseUntil.Value : session.UtcNow;
        upload.Terminate(state, code, session.UtcNow);
        var file = await db.MediaFiles.SingleAsync(f => f.Id == upload.FileId, ct);
        await db.Entry(file).ReloadAsync(ct);
        file.Delete(session.UtcNow);
        await db.SaveChangesAsync(ct);
        // There is no live source reference on a Pending/Deleted file. The upload retains its historical source FK.
        var ids = await db.MediaObjects.Where(o => o.UploadId == upload.Id && (o.State == MediaObjectState.Planned || o.State == MediaObjectState.Present))
            .Select(o => o.Id).ToArrayAsync(ct);
        foreach (var id in ids) { await session.RetireAsync(id, notBefore, ct); }
    }
}
