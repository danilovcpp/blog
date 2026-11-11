using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Shared;
using SkiaSharp;

namespace BlogEngine.Infrastructure.Media;

public sealed class PublicationImageRenderer(IMediaImageCodec codec, IMediaSpool spool) : IPublicationImageRenderer
{
    public async Task<NormalizedMediaImage> RenderAsync(IMediaSpoolFile source, string contentType, CancellationToken ct)
    {
        var normalized = await codec.NormalizeAsync(source, contentType, ct);
        if (Math.Max(normalized.Width, normalized.Height) <= 2560) { return normalized; }
        await using var content = normalized.Content;
        await using var input = content.OpenRead();
        using var bitmap = SKBitmap.Decode(input) ?? throw DomainError.Validation("content");
        var scale = 2560.0 / Math.Max(normalized.Width, normalized.Height);
        var width = Math.Max(1, (int)Math.Round(normalized.Width * scale));
        var height = Math.Max(1, (int)Math.Round(normalized.Height * scale));
        using var resized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(bitmap, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        var png = await spool.CreateAsync(checked((long)width * height * 5 + 65536), (target, token) =>
        {
            token.ThrowIfCancellationRequested();
            using var stream = new SKManagedWStream(target); using var pixels = resized.PeekPixels();
            if (!pixels.Encode(stream, new SKPngEncoderOptions())) { throw DomainError.Validation("content"); }
            return Task.CompletedTask;
        }, ct);
        return new(png, width, height);
    }
}
