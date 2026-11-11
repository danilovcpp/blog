using System.Buffers.Binary;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Shared;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace BlogEngine.Infrastructure.Media;

public sealed class SkiaImageCodec(IMediaSpool spool, IOptions<MediaOptions> options) : IMediaImageCodec, IDisposable
{
    private readonly SemaphoreSlim decoders = new(options.Value.Processing.Concurrency);

    public async Task<NormalizedMediaImage> NormalizeAsync(IMediaSpoolFile source, string declaredContentType, CancellationToken ct)
    {
        await decoders.WaitAsync(ct);
        try
        {
            await using var input = source.OpenRead();
            await ValidateContainerAsync(input, declaredContentType, ct);
            input.Position = 0;
            using var codec = SKCodec.Create(input);
            if (codec is null || codec.FrameCount > 1 || codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Jpeg => declaredContentType != "image/jpeg",
                SKEncodedImageFormat.Png => declaredContentType != "image/png",
                SKEncodedImageFormat.Webp => declaredContentType != "image/webp",
                _ => true,
            }) { throw DomainError.Validation("content"); }
            var info = codec.Info;
            if (info.Width is < 1 or > 16384 || info.Height is < 1 or > 16384 || (long)info.Width * info.Height > 40_000_000)
            { throw DomainError.Validation("content"); }
            // Decode into a fixed four-byte layout; source color profiles and metadata are never copied.
            using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) { throw DomainError.Validation("content"); }
            ct.ThrowIfCancellationRequested();
            var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var width = swap ? info.Height : info.Width; var height = swap ? info.Width : info.Height;
            using var oriented = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(oriented))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.SetMatrix(Orientation(codec.EncodedOrigin, info.Width, info.Height));
                canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            }
            var png = await spool.CreateAsync(checked((long)width * height * 5 + 65536), (target, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = new SKManagedWStream(target);
                using var pixels = oriented.PeekPixels();
                if (!pixels.Encode(stream, new SKPngEncoderOptions())) { throw DomainError.Validation("content"); }
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, ct);
            return new(png, width, height);
        }
        finally { decoders.Release(); }
    }

    private static SKMatrix Orientation(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static async Task ValidateContainerAsync(Stream input, string declared, CancellationToken ct)
    {
        var header = new byte[12];
        try { await input.ReadExactlyAsync(header, ct); }
        catch (EndOfStreamException) { throw DomainError.Validation("content"); }
        if (declared == "image/jpeg")
        {
            if (header[0] != 0xff || header[1] != 0xd8 || header[2] != 0xff) { throw DomainError.Validation("content"); }
            input.Position = input.Length - 2;
            if (input.ReadByte() != 0xff || input.ReadByte() != 0xd9) { throw DomainError.Validation("content"); }
            return;
        }
        if (declared == "image/png" && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            input.Position = 8;
            var chunk = new byte[8];
            var payload = new byte[64 * 1024];
            var checksum = new byte[4];
            var ended = false;
            while (input.Position < input.Length)
            {
                if (input.Length - input.Position < 12) { throw DomainError.Validation("content"); }
                await input.ReadExactlyAsync(chunk, ct);
                var length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                if (length > input.Length - input.Position - 4 || chunk.AsSpan(4, 4).SequenceEqual("acTL"u8)) { throw DomainError.Validation("content"); }
                var crc = UpdateCrc(uint.MaxValue, chunk.AsSpan(4, 4));
                var remaining = (long)length;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, payload.Length);
                    await input.ReadExactlyAsync(payload.AsMemory(0, count), ct); crc = UpdateCrc(crc, payload.AsSpan(0, count)); remaining -= count;
                }
                await input.ReadExactlyAsync(checksum, ct);
                if (~crc != BinaryPrimitives.ReadUInt32BigEndian(checksum)) { throw DomainError.Validation("content"); }
                if (chunk.AsSpan(4, 4).SequenceEqual("IEND"u8)) { ended = length == 0 && input.Position == input.Length; break; }
            }
            if (!ended) { throw DomainError.Validation("content"); }
            return;
        }
        if (declared == "image/webp" && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)) + 8L == input.Length)
        {
            var chunk = new byte[8];
            while (input.Position < input.Length)
            {
                if (input.Length - input.Position < 8) { throw DomainError.Validation("content"); }
                await input.ReadExactlyAsync(chunk, ct);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                var padded = length + (long)(length & 1);
                if (padded > input.Length - input.Position || chunk.AsSpan(0, 4).SequenceEqual("ANIM"u8) || chunk.AsSpan(0, 4).SequenceEqual("ANMF"u8))
                { throw DomainError.Validation("content"); }
                if (chunk.AsSpan(0, 4).SequenceEqual("VP8X"u8))
                {
                    if (length < 1 || (input.ReadByte() & 2) != 0) { throw DomainError.Validation("content"); }
                    input.Position += padded - 1;
                }
                else { input.Position += padded; }
            }
            return;
        }
        throw DomainError.Validation("content");
    }

    public void Dispose() => decoders.Dispose();

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u); }
        }
        return crc;
    }
}
