using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Application;

public sealed class UploadContentService(IUploadTransferStore store, IMediaSpool spool, IMediaObjectStorage storage)
{
    public async Task PutAsync(CurrentActor actor, Guid id, Func<MediaPurpose, CancellationToken, Task<IMediaSpoolFile>> readBody, CancellationToken ct)
    {
        var claim = await store.ClaimAsync(actor.Id, id, ct);
        string? terminalError = null;
        try
        {
            await using var transfer = await spool.AcquireTransferAsync(ct);
            await using var content = await readBody(claim.Purpose, transfer.CancellationToken);
            if (claim.IsReplay)
            {
                if (content.Length != claim.ExpectedSize || !content.Sha256.Span.SequenceEqual(claim.AcceptedHash))
                { throw new DomainError("content_conflict"); }
                return;
            }
            if (content.Length != claim.ExpectedSize) { throw DomainError.Validation("declaredSize"); }
            await storage.PutAsync(MediaBucketKind.Originals, claim.ObjectKey!, content, transfer.CancellationToken);
            await store.AcceptAsync(claim, content, transfer.CancellationToken);
        }
        catch (DomainError error) when (error.Code is "request_too_large" or "validation_failed")
        { terminalError = error.Code; throw; }
        catch (IOException) { throw new DomainError("media_storage_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DomainError("media_storage_unavailable"); }
        finally
        {
            // A disconnected HTTP client must not prevent retirement of an uncertain S3 attempt.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.ReleaseAsync(claim, claim.IsReplay ? null : terminalError, cleanup.Token);
        }
    }
}
