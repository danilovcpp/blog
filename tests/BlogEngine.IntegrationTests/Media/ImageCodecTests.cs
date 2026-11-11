using System.Buffers.Binary;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Media;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace BlogEngine.IntegrationTests.Media;

// This same suite also runs inside the production ASP.NET Linux base image (media-codec-validation target).
public sealed class ImageCodecTests
{
    [Theory]
    [InlineData(3000, 1500, 2560, 1280)]
    [InlineData(3, 2, 3, 2)]
    public async Task PublicationRendererFitsWithoutUpscaleAndPreservesAlpha(int width, int height, int expectedWidth, int expectedHeight)
    {
        var ct = TestContext.Current.CancellationToken; var options = MediaSpoolTests.Settings();
        options.Spool.BudgetBytes = 128 * 1024 * 1024;
        await using var spool = MediaSpoolTests.Spool(options); await spool.StartAsync(ct);
        using var codec = new SkiaImageCodec(spool, Options.Create(options));
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(new SKColor(10, 20, 30, 100));
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        await using var stream = encoded.AsStream();
        await using var source = await spool.CaptureAsync(stream, encoded.Size, ct);
        var result = await new PublicationImageRenderer(codec, spool).RenderAsync(source, "image/png", ct);
        await using var content = result.Content; await using var input = content.OpenRead();
        using var rendered = SKBitmap.Decode(input);
        Assert.Equal(expectedWidth, rendered.Width); Assert.Equal(expectedHeight, rendered.Height); Assert.Equal(100, rendered.GetPixel(0, 0).Alpha);
    }

    private static byte[] Image(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(3, 2);
        bitmap.Erase(new SKColor(10, 20, 30, 100));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(format, 100);
        return data.ToArray();
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public async Task FullyDecodesAndProducesFreshPngWithoutSourceMetadata(SKEncodedImageFormat format, string mime)
    {
        var ct = TestContext.Current.CancellationToken; var options = MediaSpoolTests.Settings();
        await using var spool = MediaSpoolTests.Spool(options); await spool.StartAsync(ct);
        using var codec = new SkiaImageCodec(spool, Options.Create(options));
        var bytes = Image(format);
        await using var source = await spool.CaptureAsync(new MemoryStream(bytes), bytes.Length, ct);
        var result = await codec.NormalizeAsync(source, mime, ct); await using var output = result.Content;
        Assert.Equal(3, result.Width); Assert.Equal(2, result.Height);
        await using var input = output.OpenRead(); using var normalized = SKBitmap.Decode(input);
        Assert.NotNull(normalized); if (format == SKEncodedImageFormat.Png) { Assert.Equal(100, normalized.GetPixel(0, 0).Alpha); }
        await using var encoded = output.OpenRead(); using var container = SKCodec.Create(encoded);
        Assert.Equal(SKEncodedImageFormat.Png, container.EncodedFormat); Assert.Equal(SKEncodedOrigin.TopLeft, container.EncodedOrigin);
    }

    [Fact]
    public async Task AppliesExifOrientationBeforeEncoding()
    {
        var ct = TestContext.Current.CancellationToken; var options = MediaSpoolTests.Settings();
        await using var spool = MediaSpoolTests.Spool(options); await spool.StartAsync(ct);
        using var codec = new SkiaImageCodec(spool, Options.Create(options));
        var jpeg = Image(SKEncodedImageFormat.Jpeg);
        // APP1 Exif, little-endian TIFF, orientation=6 (90 degrees clockwise).
        byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        var oriented = jpeg[..2].Concat(exif).Concat(jpeg[2..]).ToArray();
        await using var source = await spool.CaptureAsync(new MemoryStream(oriented), oriented.Length, ct);
        var result = await codec.NormalizeAsync(source, "image/jpeg", ct); await using var output = result.Content;
        Assert.Equal(2, result.Width); Assert.Equal(3, result.Height);
    }

    [Fact]
    public async Task RejectsCorruptMismatchedSvgAnimatedContainersAndDimensionsBeforeAllocation()
    {
        var ct = TestContext.Current.CancellationToken; var options = MediaSpoolTests.Settings();
        await using var spool = MediaSpoolTests.Spool(options); await spool.StartAsync(ct);
        using var codec = new SkiaImageCodec(spool, Options.Create(options));
        var png = Image(SKEncodedImageFormat.Png); var webp = Image(SKEncodedImageFormat.Webp);
        async Task Reject(byte[] bytes, string mime)
        {
            await using var source = await spool.CaptureAsync(new MemoryStream(bytes), bytes.Length, ct);
            Assert.Equal("validation_failed", (await Assert.ThrowsAsync<DomainError>(() => codec.NormalizeAsync(source, mime, ct))).Code);
        }
        await Reject("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "image/png");
        await Reject(png, "image/jpeg"); await Reject(png[..^5], "image/png");
        // An acTL chunk is rejected even if the native decoder reports only a single frame.
        byte[] animation = [0, 0, 0, 8, 97, 99, 84, 76, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        await Reject(png[..33].Concat(animation).Concat(png[33..]).ToArray(), "image/png");
        var animatedWebp = webp.Concat(new byte[] { 65, 78, 73, 77, 0, 0, 0, 0 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(animatedWebp.AsSpan(4), (uint)animatedWebp.Length - 8);
        await Reject(animatedWebp, "image/webp");
        foreach (var (width, height) in new[] { (16385, 1), (8000, 5001), (0, 2) })
        {
            var huge = png.ToArray(); BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(16), width); BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(20), height);
            BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(29), Crc(huge.AsSpan(12, 17)));
            await Reject(huge, "image/png");
        }
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u); } }
        return ~crc;
    }
}
