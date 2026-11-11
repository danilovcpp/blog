namespace BlogEngine.Core.Media.Contracts;

public sealed record NormalizedMediaImage(IMediaSpoolFile Content, int Width, int Height);

public interface IMediaImageCodec
{
    Task<NormalizedMediaImage> NormalizeAsync(IMediaSpoolFile source, string declaredContentType, CancellationToken ct);
}
