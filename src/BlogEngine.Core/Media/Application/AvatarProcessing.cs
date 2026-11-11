using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;

namespace BlogEngine.Core.Media.Application;

public interface IAvatarRenderer
{
    Task<(NormalizedMediaImage Small, NormalizedMediaImage Large)> RenderAsync(IMediaSpoolFile source, string contentType, CancellationToken ct);
}
public interface IAvatarCompletionStore
{
    Task<(MediaObject Small, MediaObject Large)> PlanAsync(MediaProcessingClaim claim, CancellationToken ct);
    Task CompleteAsync(MediaProcessingClaim claim, Guid smallId, NormalizedMediaImage small, Guid largeId, NormalizedMediaImage large, CancellationToken ct);
}

public sealed class AvatarProcessing(IMediaObjectStorage storage, IMediaSpool spool, IAvatarRenderer renderer, IAvatarCompletionStore store) : IMediaUploadProcessor
{
    public MediaPurpose Purpose => MediaPurpose.Avatar;
    public async Task ProcessAsync(MediaProcessingClaim claim, CancellationToken ct)
    {
        await using var transfer = await spool.AcquireTransferAsync(ct);
        await using var source = await storage.GetVerifiedAsync(claim.Bucket, claim.Key, claim.Length, claim.Sha256, transfer.CancellationToken);
        var images = await renderer.RenderAsync(source, claim.ContentType, ct);
        await using var small = images.Small.Content;
        await using var large = images.Large.Content;
        var plans = await store.PlanAsync(claim, ct);
        await storage.PutAsync(plans.Small.BucketKind, plans.Small.ObjectKey, small, transfer.CancellationToken);
        await storage.PutAsync(plans.Large.BucketKind, plans.Large.ObjectKey, large, transfer.CancellationToken);
        await store.CompleteAsync(claim, plans.Small.Id, images.Small, plans.Large.Id, images.Large, ct);
    }
}
