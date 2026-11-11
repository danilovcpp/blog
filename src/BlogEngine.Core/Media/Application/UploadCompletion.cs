using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Application;

public sealed class UploadCompletion(IMediaProcessingStore store, IEnumerable<IMediaUploadProcessor> processors, IMediaProcessingLease lease)
{
    public async Task<UploadSnapshot> RequestAsync(CurrentActor actor, Guid id, CancellationToken ct)
    {
        var attempt = await store.RequestAsync(actor.Id, id, ct);
        if (attempt.Error is { } code) { throw new DomainError(code); }
        return attempt.Upload!;
    }

    public async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        var claim = await store.ClaimAsync(id, ct);
        if (claim is null) { return; }
        var processor = processors.Single(p => p.Purpose == claim.Purpose);
        try
        {
            await lease.RunAsync(claim, async token =>
            {
                await processor.VerifyAsync(claim, token);
                await processor.FinalizeAsync(claim, token);
            }, ct);
        }
        catch (MediaStorageException) { await store.RetryAsync(claim, ct); }
        catch (DomainError error) when (error.Code is "media_capacity_unavailable" or "media_storage_unavailable")
        { await store.RetryAsync(claim, ct); }
        catch (DomainError error) when (error.Code is "concurrency_conflict" or "upload_expired")
        { /* A newer lease or terminal transition owns the result. */ }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await store.RetryAsync(claim, ct); }
    }
}

public sealed class AttachmentProcessing(IMediaObjectStorage storage, IMediaSpool spool, IMediaProcessingStore store) : IMediaUploadProcessor
{
    public MediaPurpose Purpose => MediaPurpose.Attachment;
    public async Task VerifyAsync(MediaProcessingClaim claim, CancellationToken ct)
    {
        await using var transfer = await spool.AcquireTransferAsync(ct);
        await using var source = await storage.GetVerifiedAsync(claim.Bucket, claim.Key, claim.Length, claim.Sha256, transfer.CancellationToken);
    }
    public Task FinalizeAsync(MediaProcessingClaim claim, CancellationToken ct) => store.CompleteAttachmentAsync(claim, ct);
}
