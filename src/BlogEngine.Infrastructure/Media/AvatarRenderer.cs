using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Shared;
using SkiaSharp;

namespace BlogEngine.Infrastructure.Media;

public sealed class AvatarRenderer(IMediaImageCodec codec, IMediaSpool spool) : IAvatarRenderer
{
    public async Task<(NormalizedMediaImage Small, NormalizedMediaImage Large)> RenderAsync(IMediaSpoolFile source, string contentType, CancellationToken ct)
    {
        var normalized = await codec.NormalizeAsync(source, contentType, ct);
        await using var content = normalized.Content;
        await using var input = content.OpenRead();
        using var bitmap = SKBitmap.Decode(input) ?? throw DomainError.Validation("content");
        var side = Math.Min(bitmap.Width, bitmap.Height);
        var left = (bitmap.Width - side) / 2f; var top = (bitmap.Height - side) / 2f;
        var crop = new SKRect(left, top, left + side, top + side);
        var small = await RenderSizeAsync(256);
        try { return (small, await RenderSizeAsync(512)); }
        catch { await small.Content.DisposeAsync(); throw; }

        async Task<NormalizedMediaImage> RenderSizeAsync(int size)
        {
            ct.ThrowIfCancellationRequested();
            using var resized = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(resized))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(bitmap, crop, new SKRect(0, 0, size, size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            var png = await spool.CreateAsync(checked((long)size * size * 5 + 65536), (target, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = new SKManagedWStream(target); using var pixels = resized.PeekPixels();
                if (!pixels.Encode(stream, new SKPngEncoderOptions())) { throw DomainError.Validation("content"); }
                return Task.CompletedTask;
            }, ct);
            return new(png, size, size);
        }
    }
}
