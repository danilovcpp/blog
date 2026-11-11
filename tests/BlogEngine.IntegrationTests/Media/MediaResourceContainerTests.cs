using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Infrastructure.Media;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlogEngine.IntegrationTests.Media;

// The validation script runs this class under a real 1 GiB cgroup limit and kills its crash mode.
[Collection("Media transfers")]
public sealed class MediaResourceContainerTests
{
    private const int TransferBytes = 50 * 1024 * 1024;

    [Fact]
    public async Task FourFiftyMiBTransfersAndNearLimitDecodeFitBudgetAndRestartReclaimsSpool()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistent = Environment.GetEnvironmentVariable("BLOG_MEDIA_RESOURCE_SPOOL");
        var crash = Environment.GetEnvironmentVariable("BLOG_MEDIA_CRASH") == "1";
        await using var identity = new JwksFixture(); await identity.InitializeAsync();
        var probes = new TransferProbes(crash);
        await using var api = new ApiFactory(ApiFactory.UnreachableDatabase, identity) { MediaSpoolDirectory = persistent, MediaProbes = probes };
        api.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var client = api.CreateClient();
        var settings = api.Services.GetRequiredService<IOptions<MediaOptions>>().Value;
        var spool = api.Services.GetRequiredService<IMediaSpool>();
        Assert.True(await spool.CheckReadinessAsync(ct));
        Assert.Empty(Directory.EnumerateFiles(settings.Spool.Directory, "*.spool"));
        if (crash)
        {
            await SendAsync(client, ct);
            throw new InvalidOperationException("Crash probe must be terminated while the API is writing the spool file.");
        }
        var bytes = EncodeNearLimit();
        await using var encoded = await spool.CaptureAsync(new MemoryStream(bytes), bytes.Length, ct);
        var transfers = Enumerable.Range(0, 4).Select(_ => SendAsync(client, ct)).ToArray();
        try
        {
            await probes.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var codec = api.Services.GetRequiredService<IMediaImageCodec>();
            var decoded = await codec.NormalizeAsync(encoded, "image/png", ct);
            await using var normalized = decoded.Content;
            Assert.Equal(40_000_000, (long)decoded.Width * decoded.Height);
            var peakFile = "/sys/fs/cgroup/memory.peak";
            if (persistent is not null)
            {
                var limit = long.Parse(await File.ReadAllTextAsync("/sys/fs/cgroup/memory.max", ct), System.Globalization.CultureInfo.InvariantCulture);
                var peak = long.Parse(await File.ReadAllTextAsync(peakFile, ct), System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(1_073_741_824, limit); Assert.InRange(peak, 1, limit - 1);
                await File.WriteAllTextAsync(Path.Combine(persistent, "resources.json"), JsonSerializer.Serialize(new { peakBytes = peak, limitBytes = limit, transfers = 4, transferBytes = TransferBytes, pixels = 40_000_000, reclaimed = true }), ct);
            }
        }
        finally { probes.Release.TrySetResult(); await Task.WhenAll(transfers); }
    }

    private static async Task SendAsync(HttpClient client, CancellationToken ct)
    {
        using var content = new StreamingBody();
        using var response = await client.PutAsync("/__media/resource", content, ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed class StreamingBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = TransferBytes; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            for (var i = 0; i < TransferBytes / buffer.Length; i++) { await stream.WriteAsync(buffer, cancellationToken); }
        }
    }

    private sealed class TransferProbes(bool crash) : IStartupFilter
    {
        private int active;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapPut("/__media/resource", CaptureAsync));
            next(app);
        };

        private async Task CaptureAsync(HttpContext context)
        {
            var maximum = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (maximum is not null) { maximum.MaxRequestBodySize = TransferBytes; }
            var spool = context.RequestServices.GetRequiredService<IMediaSpool>();
            await using var transfer = await spool.AcquireTransferAsync(context.RequestAborted);
            var token = transfer.CancellationToken;
            await using var file = await spool.CreateAsync(TransferBytes, async (target, ct) =>
            {
                if (crash)
                {
                    var first = new byte[64 * 1024];
                    await context.Request.Body.ReadExactlyAsync(first, ct);
                    await target.WriteAsync(first, ct); await target.FlushAsync(ct);
                    var path = context.RequestServices.GetRequiredService<IOptions<MediaOptions>>().Value.Spool.Directory;
                    await File.WriteAllTextAsync(Path.Combine(path, "crash.ready"), "ready", ct);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                await context.Request.Body.CopyToAsync(target, 64 * 1024, ct);
            }, token);
            Assert.Equal(TransferBytes, file.Length);
            if (Interlocked.Increment(ref active) == 4) { Ready.SetResult(); }
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
    }

    private static byte[] EncodeNearLimit()
    {
        const int width = 8_000, height = 5_000;
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6; // RGBA
        WriteChunk("IHDR"u8, header);
        using var data = new MemoryStream();
        using (var compressed = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
        {
            // Generate a valid near-limit image without allocating another full bitmap in the measured process.
            var row = new byte[1 + width * 4];
            for (var y = 0; y < height; y++) { compressed.Write(row); }
        }
        WriteChunk("IDAT"u8, data.ToArray());
        WriteChunk("IEND"u8, []);
        return png.ToArray();

        void WriteChunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
        {
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(size, payload.Length);
            png.Write(size);
            var chunk = new byte[4 + payload.Length];
            type.CopyTo(chunk);
            payload.CopyTo(chunk.AsSpan(4));
            png.Write(chunk);
            BinaryPrimitives.WriteUInt32BigEndian(size, ImageCodecTests.Crc(chunk));
            png.Write(size);
        }
    }
}
