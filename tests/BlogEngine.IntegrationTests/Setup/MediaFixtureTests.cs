using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using BlogEngine.Api.Contracts.Accounts;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BlogEngine.IntegrationTests.Setup;

[Trait("Category", "Setup")]
public sealed class MediaFixtureTests(PostgresFixture postgres, GarageFixture garage)
    : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    [Fact]
    public async Task ProvisioningIsRepeatableAndRuntimeHasOnlyPrivateBucketAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await garage.ProvisionAsync(ct);
        using var s3 = garage.CreateClient();
        foreach (var bucket in garage.Buckets)
        {
            using var sentinel = await s3.GetObjectAsync(bucket, GarageFixture.SentinelKey, ct);
            using var bytes = new MemoryStream();
            await sentinel.ResponseStream.CopyToAsync(bytes, ct);
            Assert.Equal(GarageFixture.SentinelLength, bytes.Length);
            Assert.Equal(GarageFixture.SentinelSha256, Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
            using var anonymous = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var denied = await anonymous.GetAsync(new Uri(garage.Endpoint, bucket + "/" + GarageFixture.SentinelKey), ct);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        var forbidden = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.PutBucketAsync("unprivileged-bucket", ct));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await garage.AssertRuntimePermissionsAsync(ct);
    }

    [Fact]
    public async Task BytesSurviveControlledGarageOutageAndRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var s3 = garage.CreateClient();
        var key = Guid.NewGuid().ToString("N");
        var bytes = Encoding.UTF8.GetBytes("isolated media fixture bytes");
        using var body = new MemoryStream(bytes);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = garage.OriginalsBucket,
            Key = key,
            InputStream = body,
            UseChunkEncoding = false,
            MD5Digest = Convert.ToBase64String(MD5.HashData(bytes)),
        }, ct);
        await garage.StopAsync(ct);
        try { await Assert.ThrowsAnyAsync<Exception>(() => s3.GetObjectAsync(garage.OriginalsBucket, key, ct)); }
        finally { await garage.RestartAsync(ct); }
        using var restartedClient = garage.CreateClient();
        using var restored = await restartedClient.GetObjectAsync(garage.OriginalsBucket, key, ct);
        using var output = new MemoryStream();
        await restored.ResponseStream.CopyToAsync(output, ct);
        Assert.Equal(bytes, output.ToArray());
        await restartedClient.DeleteObjectAsync(garage.OriginalsBucket, key, ct);
        var missing = await Assert.ThrowsAsync<NoSuchKeyException>(() => restartedClient.GetObjectAsync(garage.OriginalsBucket, key, ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ProvisionerRepairsExcessPrivilegesAndRejectsUnexpectedGrants()
    {
        var ct = TestContext.Current.CancellationToken;
        await garage.VerifyProvisioningGuardsAsync(ct);
        await garage.AssertRuntimePermissionsAsync(ct);
    }

    [Fact]
    public async Task TwoApiHostsShareStorageDatabaseIdentityAndControlledClock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hosts = await MultiHostFixture.StartAsync(postgres, ct, garage);
        var first = hosts.First.Factory.Services.GetRequiredService<IConfiguration>();
        var second = hosts.Second.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(garage.Endpoint.AbsoluteUri, first["Media:Storage:Endpoint"]);
        Assert.Same(garage, hosts.First.Factory.MediaStorage);
        Assert.Same(garage, hosts.Second.MediaStorage);
        Assert.Equal(first["Media:Storage:OriginalsBucket"], second["Media:Storage:OriginalsBucket"]);
        Assert.Same(hosts.First.Factory.Time, hosts.Second.Time);
        Assert.Same(hosts.Clock, hosts.First.Factory.ViewClock);
        Assert.Same(hosts.Clock, hosts.Second.ViewClock);
        using var live = await hosts.Replica.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var actor = await hosts.First.ActorAsync(LocalRole.Reader, ct);
        hosts.Replica.DefaultRequestHeaders.Authorization = actor.DefaultRequestHeaders.Authorization;
        var barrier = new ConcurrencyBarrier(2);
        var ids = await Task.WhenAll(ReadMeAsync(actor), ReadMeAsync(hosts.Replica));
        Assert.Equal(ids[0], ids[1]);

        async Task<Guid> ReadMeAsync(HttpClient client)
        {
            await barrier.SignalAndWaitAsync(ct);
            var me = await client.GetFromJsonAsync<MeDto>("/api/v1/me", ct);
            Assert.NotNull(me);
            return me.Id;
        }
    }
}
