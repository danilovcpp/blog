using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;

namespace BlogEngine.Core.Media.Application;

public interface IPublicationImageRenderer
{
    Task<NormalizedMediaImage> RenderAsync(IMediaSpoolFile source, string contentType, CancellationToken ct);
}

public interface IPublicationImageStore
{
    Task<MediaObject> PlanDisplayAsync(MediaProcessingClaim claim, CancellationToken ct);
    Task CompleteImageAsync(MediaProcessingClaim claim, Guid displayId, long length, byte[] sha256, int width, int height, CancellationToken ct);
}

public sealed class PublicationImageProcessing(IMediaObjectStorage storage, IMediaSpool spool,
    IPublicationImageRenderer renderer, IPublicationImageStore store) : IMediaUploadProcessor
{
    public MediaPurpose Purpose => MediaPurpose.PublicationImage;
    public async Task ProcessAsync(MediaProcessingClaim claim, CancellationToken ct)
    {
        await using var transfer = await spool.AcquireTransferAsync(ct);
        await using var source = await storage.GetVerifiedAsync(claim.Bucket, claim.Key, claim.Length, claim.Sha256, transfer.CancellationToken);
        var image = await renderer.RenderAsync(source, claim.ContentType, ct);
        await using var display = image.Content;
        var plan = await store.PlanDisplayAsync(claim, ct);
        await storage.PutAsync(plan.BucketKind, plan.ObjectKey, display, transfer.CancellationToken);
        await store.CompleteImageAsync(claim, plan.Id, display.Length, display.Sha256.ToArray(), image.Width, image.Height, ct);
    }
}
