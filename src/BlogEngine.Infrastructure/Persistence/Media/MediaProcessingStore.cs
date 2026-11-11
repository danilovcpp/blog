using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class MediaProcessingStore(BlogDbContext db, DatabaseClock clock, IMediaWriteStore writes,
    IOptions<MediaOptions> options) : IMediaProcessingStore, IPublicationImageStore
{
    public async Task<UploadCompletionAttempt> RequestAsync(Guid ownerId, Guid uploadId, CancellationToken ct)
    {
        var known = await MediaUploadTransitions.FindOwnedAsync(db, ownerId, uploadId, ct);
        if (known.State == MediaUploadState.Completed) { return new(await SnapshotAsync(known, ct), null); }
        return await writes.ExecuteAsync(new(ownerId, RoleActorId: ownerId, FileIds: [known.FileId], DemandRole: false), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(uploadId, token) ?? throw new DomainError("resource_not_found");
            if (upload.State == MediaUploadState.Completed) { return new UploadCompletionAttempt(await SnapshotAsync(upload, token), null); }
            var error = await CheckTransitionAsync(session, upload, token);
            if (error is not null) { return new(null, error); }
            if (upload.State == MediaUploadState.ContentReceived) { upload.StartProcessing(session.UtcNow); }
            else if (upload.State != MediaUploadState.Processing) { return new(null, "invalid_state"); }
            return new(await SnapshotAsync(upload, token), null);
        }, ct);
    }

    private async Task<string?> CheckTransitionAsync(IMediaWriteSession session, MediaUpload upload, CancellationToken ct)
    {
        if (!upload.IsActive) { return "invalid_state"; }
        if (upload.ExpiresAt <= session.UtcNow)
        {
            await MediaUploadTransitions.TerminateAsync(db, session, upload, MediaUploadState.Expired, "upload_expired", ct);
            return "invalid_state";
        }
        if (upload.Purpose != MediaPurpose.Avatar && (session.RoleActor is null || (!session.RoleActor.HasRole(LocalRole.Author) && !session.RoleActor.HasRole(LocalRole.Admin))))
        {
            await MediaUploadTransitions.TerminateAsync(db, session, upload, MediaUploadState.Rejected, "forbidden", ct);
            return "forbidden";
        }
        return null;
    }

    private async Task<UploadSnapshot> SnapshotAsync(MediaUpload upload, CancellationToken ct) =>
        UploadSnapshot.From(upload, await db.MediaFiles.AsNoTracking().SingleAsync(f => f.Id == upload.FileId, ct), await clock.ReadAsync(db, ct));

    public async Task<IReadOnlyList<Guid>> DueAsync(CancellationToken ct)
    {
        var now = await clock.ReadAsync(db, ct);
        return await db.MediaUploads.AsNoTracking().Where(u => u.State == MediaUploadState.Processing &&
                u.NextAttemptAt <= now && (u.LeaseUntil == null || u.LeaseUntil <= now))
            .OrderBy(u => u.NextAttemptAt).ThenBy(u => u.Id).Select(u => u.Id).Take(options.Value.Processing.BatchSize).ToArrayAsync(ct);
    }

    public async Task<MediaProcessingClaim?> ClaimAsync(Guid uploadId, CancellationToken ct)
    {
        var known = await db.MediaUploads.AsNoTracking().SingleOrDefaultAsync(u => u.Id == uploadId, ct);
        if (known is null || known.State != MediaUploadState.Processing) { return null; }
        return await writes.ExecuteAsync<MediaProcessingClaim?>(new(known.OwnerId, RoleActorId: known.OwnerId, FileIds: [known.FileId], DemandRole: false), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(uploadId, token);
            if (upload is null || upload.State != MediaUploadState.Processing || upload.LeaseUntil > session.UtcNow || upload.NextAttemptAt > session.UtcNow) { return null; }
            if (await CheckTransitionAsync(session, upload, token) is not null) { return null; }
            var generation = upload.Claim(session.UtcNow, options.Value.Processing.LeaseDuration);
            var source = await db.MediaObjects.AsNoTracking().SingleAsync(o => o.Id == upload.CurrentObjectId, token);
            var type = await db.MediaFiles.Where(f => f.Id == upload.FileId).Select(f => f.DeclaredContentType).SingleAsync(token);
            return new(upload.Id, upload.OwnerId, upload.FileId, upload.Purpose, generation, source.Id,
                source.BucketKind, source.ObjectKey, source.VerifiedLength!.Value, source.Sha256!, type);
        }, ct);
    }

    public async Task RenewAsync(MediaProcessingClaim claim, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, FileIds: [claim.FileId]), async (session, token) =>
        {
            var upload = await session.RequireFenceAsync(claim.UploadId, claim.Generation, token);
            upload.RenewLease(claim.Generation, session.UtcNow, options.Value.Processing.LeaseDuration); return 0;
        }, ct);

    public async Task CompleteAttachmentAsync(MediaProcessingClaim claim, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, RoleActorId: claim.OwnerId, FileIds: [claim.FileId], DemandRole: false), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(claim.UploadId, token) ?? throw new DomainError("resource_not_found");
            if (upload.State != MediaUploadState.Processing || upload.LeaseGeneration != claim.Generation) { return 0; }
            if (await CheckTransitionAsync(session, upload, token) is not null) { return 0; }
            await session.RequireFenceAsync(claim.UploadId, claim.Generation, token);
            if (upload.Purpose != MediaPurpose.Attachment) { throw new DomainError("invalid_state"); }
            var file = await db.MediaFiles.SingleAsync(f => f.Id == claim.FileId, token);
            await db.Entry(file).ReloadAsync(token);
            file.MakeReady(claim.SourceId, claim.ContentType, session.UtcNow);
            upload.Complete(claim.Generation, JsonSerializer.Serialize(new UploadResult(upload.Id, file.Id, file.Purpose, session.UtcNow, null)), session.UtcNow);
            // Flush the terminal upload before quota calculation so its reservation is no longer counted.
            await db.SaveChangesAsync(token);
            await session.ApplyUsageAsync(file.OriginalSize, token);
            return 0;
        }, ct);

    public async Task RetryAsync(MediaProcessingClaim claim, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, FileIds: [claim.FileId]), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(claim.UploadId, token) ?? throw new DomainError("resource_not_found");
            if (upload.State != MediaUploadState.Processing || upload.LeaseGeneration != claim.Generation) { return 0; }
            if (upload.ExpiresAt <= session.UtcNow)
            { await MediaUploadTransitions.TerminateAsync(db, session, upload, MediaUploadState.Expired, "upload_expired", token); }
            else if (upload.LeaseUntil > session.UtcNow)
            { upload.Retry(claim.Generation, session.UtcNow, TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Min(upload.Attempts, 8))))); }
            return 0;
        }, ct);

    public Task<MediaObject> PlanDisplayAsync(MediaProcessingClaim claim, CancellationToken ct) =>
        writes.ExecuteAsync(new(claim.OwnerId, FileIds: [claim.FileId]), async (session, token) =>
        {
            var upload = await session.RequireFenceAsync(claim.UploadId, claim.Generation, token);
            if (upload.Purpose != MediaPurpose.PublicationImage) { throw new DomainError("invalid_state"); }
            var obsolete = await db.MediaObjects.Where(o => o.UploadId == claim.UploadId && o.Kind == MediaObjectKind.Display &&
                (o.State == MediaObjectState.Planned || o.State == MediaObjectState.Present)).Select(o => o.Id).ToArrayAsync(token);
            foreach (var id in obsolete) { await session.RetireAsync(id, upload.LeaseUntil!.Value, token); }
            var plan = MediaObject.Plan(upload, MediaObjectKind.Display, session.UtcNow);
            await session.SaveObjectPlanAsync(plan, token);
            return plan;
        }, ct);

    public async Task CompleteImageAsync(MediaProcessingClaim claim, Guid displayId, long length, byte[] sha256, int width, int height, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, RoleActorId: claim.OwnerId, FileIds: [claim.FileId], DemandRole: false), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(claim.UploadId, token) ?? throw new DomainError("resource_not_found");
            if (upload.State != MediaUploadState.Processing || upload.LeaseGeneration != claim.Generation) { return 0; }
            if (await CheckTransitionAsync(session, upload, token) is not null) { return 0; }
            await session.RequireFenceAsync(claim.UploadId, claim.Generation, token);
            if (upload.Purpose != MediaPurpose.PublicationImage) { throw new DomainError("invalid_state"); }
            var display = await db.MediaObjects.SingleAsync(o => o.Id == displayId && o.UploadId == claim.UploadId && o.Kind == MediaObjectKind.Display && o.AttemptGeneration == claim.Generation, token);
            display.MarkPresent(length, sha256);
            await db.SaveChangesAsync(token);
            db.MediaRepresentations.Add(MediaRepresentation.Create(display, width, height));
            await db.SaveChangesAsync(token);
            var file = await db.MediaFiles.SingleAsync(f => f.Id == claim.FileId, token);
            await db.Entry(file).ReloadAsync(token);
            file.MakeReady(claim.SourceId, claim.ContentType, session.UtcNow);
            upload.Complete(claim.Generation, JsonSerializer.Serialize(new UploadResult(upload.Id, file.Id, file.Purpose, session.UtcNow, null)), session.UtcNow);
            await db.SaveChangesAsync(token);
            await session.ApplyUsageAsync(file.OriginalSize, token);
            return 0;
        }, ct);

    public async Task RejectAsync(MediaProcessingClaim claim, string code, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, FileIds: [claim.FileId]), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(claim.UploadId, token);
            if (upload is null || upload.State != MediaUploadState.Processing || upload.LeaseGeneration != claim.Generation || upload.LeaseUntil <= session.UtcNow) { return 0; }
            var expired = upload.ExpiresAt <= session.UtcNow;
            await MediaUploadTransitions.TerminateAsync(db, session, upload, expired ? MediaUploadState.Expired : MediaUploadState.Rejected,
                expired ? "upload_expired" : code, token);
            return 0;
        }, ct);
}
