using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlogEngine.IntegrationTests.Media;

public sealed class MediaBinaryTransportTests(GarageFixture garage) : IClassFixture<GarageFixture>
{
    [Theory]
    [InlineData(false, true, 200)]
    [InlineData(false, false, 404)]
    [InlineData(true, true, 503)]
    [InlineData(true, false, 404)]
    public async Task ReauthorizesAfterSuccessfulOrFailedStorageBeforeAnyHeaders(bool missing, bool stillAuthorized, int status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var identity = new JwksFixture(); await identity.InitializeAsync();
        var bytes = RandomNumberGenerator.GetBytes(1024);
        var access = new MediaDownloadAccess(MediaBucketKind.Originals, Guid.NewGuid().ToString("N"), bytes.Length, SHA256.HashData(bytes), "application/octet-stream", "имя \" файла.bin");
        var probe = new Probes(access, stillAuthorized);
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity) { MediaStorage = garage, MediaProbes = probe };
        using var client = factory.CreateClient();
        if (!missing)
        {
            var spool = factory.Services.GetRequiredService<IMediaSpool>();
            await using var file = await spool.CaptureAsync(new MemoryStream(bytes), bytes.Length, ct);
            await factory.Services.GetRequiredService<IMediaObjectStorage>().PutAsync(access.Bucket, access.Key, file, ct);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__media/content");
        request.Headers.Accept.Add(new("application/octet-stream")); request.Headers.Range = new RangeHeaderValue(0, 10);
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal(2, probe.Authorizations);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Accept-Ranges"));
        if (status == 200)
        {
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(ct)); Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal(access.FileName, response.Content.Headers.ContentDisposition.FileNameStar);
        }
        else
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            Assert.DoesNotContain(access.FileName!, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            if (status == 503)
            {
                Assert.True(response.Headers.RetryAfter!.Delta > TimeSpan.Zero);
                Assert.Contains("dependency_unavailable", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task BinaryAcceptRawPutAndInvalidOptionalBearerUseProductionPipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var identity = new JwksFixture(); await identity.InitializeAsync();
        var probe = new Probes(null, true);
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity) { MediaProbes = probe };
        using var client = factory.CreateClient();
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/__media/content"))
        {
            request.Headers.Accept.Add(new("text/html")); using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode); Assert.Equal(0, probe.Authorizations);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/__media/content"))
        {
            request.Headers.Authorization = new("Bearer", "invalid"); using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, probe.Authorizations);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Put, "/__media/upload") { Content = new ByteArrayContent(new byte[9 * 1024 * 1024]) })
        {
            request.Content.Headers.ContentType = new("application/octet-stream");
            using var response = await client.SendAsync(request, ct); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Put, "/__media/upload") { Content = new ByteArrayContent(new byte[10 * 1024 * 1024 + 1]) })
        {
            request.Content.Headers.ContentType = new("application/octet-stream");
            using var response = await client.SendAsync(request, ct); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }

    private sealed class Probes(MediaDownloadAccess? access, bool stillAuthorized) : IStartupFilter
    {
        public int Authorizations { get; private set; }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseRouting(); next(app);
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/__media/content", async (HttpContext context, MediaBinaryTransport transport) =>
                {
                    await transport.DownloadAsync(context, _ => Task.FromResult(++Authorizations == 1 || stillAuthorized ? access : null));
                }).AllowAnonymous().WithMetadata(new BinaryEndpoint("application/octet-stream"), new OptionalBearerContent());
                endpoints.MapPut("/__media/upload", async (HttpContext context, IMediaSpool spool) =>
                {
                    MediaBinaryTransport.PrepareUpload(context, MediaPurpose.PublicationImage);
                    await using var transfer = await spool.AcquireTransferAsync(context.RequestAborted);
                    await using var file = await spool.CaptureAsync(context.Request.Body, MediaRules.MaximumSize(MediaPurpose.PublicationImage), transfer.CancellationToken);
                    return Results.NoContent();
                }).AllowAnonymous().WithMetadata(new BinaryEndpoint("application/octet-stream", RawUpload: true));
            });
        };
    }
}
