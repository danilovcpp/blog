using System.Security.Cryptography;
using Amazon.S3.Model;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Media;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BlogEngine.IntegrationTests.Media;

public sealed class GarageStorageTests(GarageFixture garage) : IClassFixture<GarageFixture>
{
    [Fact]
    public async Task SignedKnownLengthPutVerifiedGetDeleteIntegrityAndCancellationWorkOnPinnedGarage()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = MediaSpoolTests.Settings();
        options.Spool.BudgetBytes = 8 * 1024 * 1024;
        new ConfigurationBuilder().AddInMemoryCollection(garage.Configuration).Build().GetSection("Media").Bind(options);
        await using var spool = MediaSpoolTests.Spool(options); await spool.StartAsync(ct);
        using var storage = new GarageObjectStorage(Options.Create(options), spool);
        await using var transfer = await spool.AcquireTransferAsync(ct);
        var key = Guid.NewGuid().ToString("N"); var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 7);
        await using var source = await spool.CaptureAsync(new MemoryStream(bytes), bytes.Length, ct);
        foreach (var bucket in new[] { MediaBucketKind.Originals, MediaBucketKind.Renditions })
        {
            await storage.PutAsync(bucket, key, source, ct);
            await using (var fetched = await storage.GetVerifiedAsync(bucket, key, bytes.Length, source.Sha256, ct))
            { Assert.Equal(source.Length, fetched.Length); Assert.Equal(source.Sha256.ToArray(), fetched.Sha256.ToArray()); }
            var wrongHash = new byte[32];
            Assert.Equal("media_integrity_failure", (await Assert.ThrowsAsync<MediaStorageException>(() => storage.GetVerifiedAsync(bucket, key, bytes.Length, wrongHash, ct))).Code);
            Assert.Equal("media_integrity_failure", (await Assert.ThrowsAsync<MediaStorageException>(() => storage.GetVerifiedAsync(bucket, key, bytes.Length - 1, source.Sha256, ct))).Code);
            await storage.DeleteAsync(bucket, key, ct); await storage.DeleteAsync(bucket, key, ct);
            Assert.Equal("media_object_missing", (await Assert.ThrowsAsync<MediaStorageException>(() => storage.GetVerifiedAsync(bucket, key, bytes.Length, source.Sha256, ct))).Code);
        }
        // Garage actually validates Content-MD5; an incorrect digest cannot become a successful PUT.
        using var client = garage.CreateClient();
        var md5Failure = await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() => client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = garage.OriginalsBucket,
            Key = key,
            InputStream = new MemoryStream(bytes),
            UseChunkEncoding = false,
            DisablePayloadSigning = false,
            MD5Digest = Convert.ToBase64String(new byte[16]),
            Headers = { ContentLength = bytes.Length },
        }, ct));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, md5Failure.StatusCode);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.PutAsync(MediaBucketKind.Originals, key, source, cancelled.Token));
    }

    [Fact]
    public async Task UnavailableGarageIsBoundedAndClassifiedWithoutLeakingEndpointOrCredentials()
    {
        var ct = TestContext.Current.CancellationToken; var options = MediaSpoolTests.Settings();
        options.Storage.Endpoint = "http://127.0.0.1:1"; options.Storage.AllowLocalHttp = true; options.Storage.RequestTimeout = TimeSpan.FromSeconds(1);
        await using var spool = MediaSpoolTests.Spool(options); await spool.StartAsync(ct);
        using var storage = new GarageObjectStorage(Options.Create(options), spool);
        var error = await Assert.ThrowsAsync<MediaStorageException>(() => storage.GetVerifiedAsync(MediaBucketKind.Originals, "safe-key", 10, new byte[32], ct));
        Assert.True(error.IsTransient); Assert.Equal("media_storage_unavailable", error.Message);
    }
}
