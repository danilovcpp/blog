using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class UploadTransferStore(BlogDbContext db, IMediaWriteStore writes, IOptions<MediaOptions> options) : IUploadTransferStore
{
    public async Task<UploadTransferClaim> ClaimAsync(Guid ownerId, Guid uploadId, CancellationToken ct)
    {
        var known = await MediaUploadTransitions.FindOwnedAsync(db, ownerId, uploadId, ct);
        return await writes.ExecuteAsync(new(ownerId, RoleActorId: ownerId, FileIds: [known.FileId], DemandRole: false), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(uploadId, token) ?? throw new DomainError("resource_not_found");
            if (upload.EffectiveState(session.UtcNow) is not (MediaUploadState.AwaitingContent or MediaUploadState.ContentReceived))
            { throw new DomainError("invalid_state"); }
            if (upload.Purpose != MediaPurpose.Avatar) { AccessPolicy.Demand(session.RoleActor!, ActorAction.CreatePublication); }
            var generation = upload.Claim(session.UtcNow, options.Value.Transfers.TotalTimeout + TimeSpan.FromSeconds(30));
            await db.SaveChangesAsync(token);
            MediaObject? plan = null;
            if (upload.State == MediaUploadState.AwaitingContent)
            {
                // Every uncertain attempt gets a new key and its own durable cleanup record.
                var abandoned = await db.MediaObjects.Where(o => o.UploadId == upload.Id && o.State == MediaObjectState.Planned).Select(o => o.Id).ToArrayAsync(token);
                foreach (var id in abandoned) { await session.RetireAsync(id, session.UtcNow, token); }
                plan = MediaObject.Plan(upload, MediaObjectKind.Source, session.UtcNow);
                await session.SaveObjectPlanAsync(plan, token);
            }
            return new UploadTransferClaim(upload.Id, ownerId, upload.FileId, upload.Purpose, generation,
                upload.ReservedBytes, upload.ContentSha256, plan?.Id, plan?.ObjectKey, upload.LeaseUntil!.Value);
        }, ct);
    }

    public async Task AcceptAsync(UploadTransferClaim claim, IMediaSpoolFile content, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, FileIds: [claim.FileId]), async (session, token) =>
        {
            var upload = await session.RequireFenceAsync(claim.UploadId, claim.Generation, token);
            var plan = await db.MediaObjects.SingleAsync(o => o.Id == claim.ObjectId, token);
            await db.Entry(plan).ReloadAsync(token);
            plan.MarkPresent(content.Length, content.Sha256.ToArray());
            await db.SaveChangesAsync(token);
            upload.AcceptContent(claim.Generation, plan.Id, content.Length, content.Sha256.ToArray(), session.UtcNow);
            return 0;
        }, ct);

    public async Task ReleaseAsync(UploadTransferClaim claim, string? terminalError, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, FileIds: [claim.FileId]), async (session, token) =>
        {
            var upload = await session.LockUploadAsync(claim.UploadId, token) ?? throw new DomainError("resource_not_found");
            if (upload.CurrentObjectId == claim.ObjectId && claim.ObjectId is not null) { return 0; }
            if (upload.IsActive && upload.LeaseGeneration == claim.Generation)
            {
                if (upload.ExpiresAt <= session.UtcNow)
                { await MediaUploadTransitions.TerminateAsync(db, session, upload, MediaUploadState.Expired, "upload_expired", token); }
                else if (terminalError is not null && upload.State == MediaUploadState.AwaitingContent)
                { await MediaUploadTransitions.TerminateAsync(db, session, upload, MediaUploadState.Rejected, terminalError, token); }
                else { upload.ReleaseTransfer(claim.Generation); }
            }
            if (claim.ObjectId is { } objectId) { await session.RetireAsync(objectId, claim.LeaseUntil, token); }
            return 0;
        }, ct);
}
