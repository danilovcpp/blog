using System.Text.Json;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class AvatarCompletionStore(BlogDbContext db, IMediaWriteStore writes) : IAvatarCompletionStore
{
    public Task<(MediaObject Small, MediaObject Large)> PlanAsync(MediaProcessingClaim claim, CancellationToken ct) =>
        writes.ExecuteAsync(new(claim.OwnerId, FileIds: [claim.FileId]), async (session, token) =>
        {
            var upload = await session.RequireFenceAsync(claim.UploadId, claim.Generation, token);
            if (upload.Purpose != MediaPurpose.Avatar) { throw new DomainError("invalid_state"); }
            var obsolete = await db.MediaObjects.Where(o => o.UploadId == claim.UploadId && o.Kind != MediaObjectKind.Source &&
                (o.State == MediaObjectState.Planned || o.State == MediaObjectState.Present)).Select(o => o.Id).ToArrayAsync(token);
            foreach (var id in obsolete) { await session.RetireAsync(id, upload.LeaseUntil!.Value, token); }
            var small = MediaObject.Plan(upload, MediaObjectKind.Avatar256, session.UtcNow);
            var large = MediaObject.Plan(upload, MediaObjectKind.Avatar512, session.UtcNow);
            await session.SaveObjectPlanAsync(small, token); await session.SaveObjectPlanAsync(large, token);
            return (small, large);
        }, ct);

    public async Task CompleteAsync(MediaProcessingClaim claim, Guid smallId, NormalizedMediaImage small, Guid largeId, NormalizedMediaImage large, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, Avatar: true, FileIds: [claim.FileId]), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(claim.UploadId, token) ?? throw new DomainError("resource_not_found");
            if (upload.State != MediaUploadState.Processing || upload.LeaseGeneration != claim.Generation) { return 0; }
            if (upload.ExpiresAt <= session.UtcNow)
            {
                await MediaUploadTransitions.TerminateAsync(db, session, upload, MediaUploadState.Expired, "upload_expired", token); return 0;
            }
            await session.RequireFenceAsync(claim.UploadId, claim.Generation, token);
            if (upload.Purpose != MediaPurpose.Avatar) { throw new DomainError("invalid_state"); }
            var state = await db.AvatarStates.SingleAsync(a => a.UserId == claim.OwnerId, token);
            await db.Entry(state).ReloadAsync(token);
            if (state.Version != upload.ExpectedAvatarVersion)
            {
                await MediaUploadTransitions.TerminateAsync(db, session, upload, MediaUploadState.Rejected, "concurrency_conflict", token); return 0;
            }
            var previous = state.CurrentFileId;
            foreach (var (id, image, kind) in new[] { (smallId, small, MediaObjectKind.Avatar256), (largeId, large, MediaObjectKind.Avatar512) })
            {
                var obj = await db.MediaObjects.SingleAsync(o => o.Id == id && o.UploadId == claim.UploadId && o.Kind == kind && o.AttemptGeneration == claim.Generation, token);
                obj.MarkPresent(image.Content.Length, image.Content.Sha256.ToArray());
                await db.SaveChangesAsync(token);
                db.MediaRepresentations.Add(MediaRepresentation.Create(obj, image.Width, image.Height));
            }
            await db.SaveChangesAsync(token);
            var file = await db.MediaFiles.SingleAsync(f => f.Id == claim.FileId, token);
            await db.Entry(file).ReloadAsync(token);
            file.MakeReady(null, claim.ContentType, session.UtcNow);
            await db.SaveChangesAsync(token);
            state.Replace(upload.ExpectedAvatarVersion!.Value, file, session.UtcNow);
            upload.Complete(claim.Generation, JsonSerializer.Serialize(new UploadResult(upload.Id, file.Id, file.Purpose, session.UtcNow, state.Version)), session.UtcNow);
            await db.SaveChangesAsync(token);
            if (previous is { } old) { await AvatarStateStore.RetireFileAsync(db, session, old, token); }
            await session.ApplyUsageAsync(file.OriginalSize, token);
            await session.RetireAsync(claim.SourceId, session.UtcNow, token);
            return 0;
        }, ct);
}
