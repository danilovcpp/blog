using System.Security.Cryptography;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlogEngine.IntegrationTests.Media;

public sealed class MediaSpoolTests
{
    internal static MediaOptions Settings() => new()
    {
        Storage = new() { Endpoint = "https://storage.fixture.test", AccessKey = "fixture-access", SecretKey = "fixture-secret" },
        Spool = new() { Directory = Path.Combine(Path.GetTempPath(), "blog-media-tests", Guid.NewGuid().ToString("N")), BudgetBytes = 1024 * 1024 },
    };
    internal static MediaSpool Spool(MediaOptions options) => new(Options.Create(options), NullLogger<MediaSpool>.Instance);

    [Fact]
    public async Task HashesLimitsCancellationAndDisposalRestorePhysicalBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = Settings(); settings.Spool.BudgetBytes = 128;
        await using var spool = Spool(settings); await spool.StartAsync(ct); Assert.True(spool.IsReady);
        var bytes = RandomNumberGenerator.GetBytes(100);
        await using (var file = await spool.CaptureAsync(new MemoryStream(bytes), 100, ct))
        {
            Assert.Equal(100, file.Length); Assert.Equal(100, spool.UsedBytes);
            Assert.Equal(SHA256.HashData(bytes), file.Sha256.ToArray()); Assert.Equal(MD5.HashData(bytes), file.Md5.ToArray());
            var error = await Assert.ThrowsAsync<DomainError>(() => spool.CaptureAsync(new MemoryStream(new byte[29]), 29, ct));
            Assert.Equal("media_capacity_unavailable", error.Code);
        }
        Assert.Equal(0, spool.UsedBytes);
        Assert.Equal("request_too_large", (await Assert.ThrowsAsync<DomainError>(() => spool.CaptureAsync(new MemoryStream(bytes), 99, ct))).Code);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => spool.CreateAsync(100, async (target, token) =>
        {
            await target.WriteAsync(bytes, token); await cancellation.CancelAsync(); token.ThrowIfCancellationRequested();
        }, cancellation.Token));
        Assert.Equal(0, spool.UsedBytes); Assert.Empty(Directory.EnumerateFiles(settings.Spool.Directory, "*.spool"));
    }

    [Fact]
    public async Task ExclusiveVolumeDoesNotDeleteAnotherOwnersActiveFileAndRestartReclaimsOnlySpoolFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = Settings();
        await using var first = Spool(settings); await first.StartAsync(ct);
        await using var file = await first.CaptureAsync(new MemoryStream(new byte[20]), 20, ct);
        await using (var conflict = Spool(settings))
        {
            await conflict.StartAsync(ct); Assert.False(conflict.IsReady);
            Assert.Single(Directory.EnumerateFiles(settings.Spool.Directory, "*.spool"));
            await Assert.ThrowsAsync<DomainError>(() => conflict.AcquireTransferAsync(ct));
        }
        await first.StopAsync(ct);
        var stale = Path.Combine(settings.Spool.Directory, "media-crashed.spool");
        var unrelated = Path.Combine(settings.Spool.Directory, "keep.txt");
        await File.WriteAllBytesAsync(stale, new byte[50], ct); await File.WriteAllTextAsync(unrelated, "keep", ct);
        await using var restarted = Spool(settings); await restarted.StartAsync(ct);
        Assert.True(restarted.IsReady); Assert.False(File.Exists(stale)); Assert.True(File.Exists(unrelated));
        Assert.Equal(0, restarted.UsedBytes);
    }

    [Fact]
    public async Task TransfersShareSlotsAndShutdownCancelsActiveWritersBeforeReleasingVolume()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = Settings(); settings.Transfers.Concurrency = 1;
        await using var spool = Spool(settings); await spool.StartAsync(ct);
        await using var transfer = await spool.AcquireTransferAsync(ct);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => spool.AcquireTransferAsync(cancelled.Token));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = spool.CreateAsync(100, async (target, token) =>
        {
            await target.WriteAsync(new byte[10], token); entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); await spool.StopAsync(ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.Equal(0, spool.UsedBytes); Assert.Empty(Directory.EnumerateFiles(settings.Spool.Directory, "*.spool"));
    }

    [Fact]
    public void OptionsRejectUnsafeEndpointsAndInvalidCapacityWithoutEchoingSecrets()
    {
        var validator = new MediaOptionsValidator(); var settings = Settings();
        Assert.True(validator.Validate(null, settings).Succeeded);
        settings.Storage.Endpoint = "http://public.example.org"; settings.Storage.AllowLocalHttp = true;
        Assert.True(validator.Validate(null, settings).Failed);
        settings.Storage.Endpoint = "http://127.0.0.1:3900";
        Assert.True(validator.Validate(null, settings).Succeeded);
        settings.Storage.RenditionsBucket = settings.Storage.OriginalsBucket; settings.Processing.RenewalInterval = settings.Processing.LeaseDuration;
        var error = validator.Validate(null, settings); Assert.True(error.Failed);
        Assert.DoesNotContain(settings.Storage.SecretKey, error.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KestrelSizeFailureReachesFencedUploadScenarioUnchanged()
    {
        var ct = TestContext.Current.CancellationToken; var settings = Settings();
        await using var spool = Spool(settings); await spool.StartAsync(ct);
        var expected = new BadHttpRequestException("body exceeds purpose limit", StatusCodes.Status413PayloadTooLarge);
        var actual = await Assert.ThrowsAsync<BadHttpRequestException>(() => spool.CreateAsync(10, (_, _) => Task.FromException(expected), ct));
        Assert.Same(expected, actual); Assert.Equal(0, spool.UsedBytes); Assert.True(await spool.CheckReadinessAsync(ct));
        Assert.Empty(Directory.EnumerateFiles(settings.Spool.Directory, "*.spool"));
    }
}
