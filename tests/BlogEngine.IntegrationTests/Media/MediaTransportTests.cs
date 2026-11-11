using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlogEngine.IntegrationTests.Media;

[Collection("Media transfers")]
public sealed class MediaTransportTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KestrelEnforcesActualPurposeLimitAndPreservesAcceptedReplay(bool chunked)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var kestrel = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage };
        kestrel.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        using var client = kestrel.CreateClient(); client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var limit = MediaRules.MaximumSize(MediaPurpose.Attachment);
        foreach (var (declared, actual, expected, state) in new[]
        {
            (limit, limit, HttpStatusCode.NoContent, "ContentReceived"),
            (3L, 2L, HttpStatusCode.BadRequest, "Rejected"),
            (1L, limit + 1, HttpStatusCode.RequestEntityTooLarge, "Rejected"),
        })
        {
            var upload = await UploadContractTests.InitAsync(client, declared, ct); var id = upload.GetProperty("id").GetGuid();
            using var response = await SendAsync(client, id, actual, chunked, ct);
            Assert.Equal(expected, response.StatusCode);
            var status = await PublishingFixture.JsonAsync(await client.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
            Assert.Equal(state, status.GetProperty("state").GetString());
            if (state == "Rejected")
            { Assert.Equal(expected == HttpStatusCode.BadRequest ? "validation_failed" : "request_too_large", status.GetProperty("errorCode").GetString()); }
            else
            {
                using var oversizeReplay = await SendAsync(client, id, limit + 1, chunked, ct);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizeReplay.StatusCode);
                using var exactReplay = await SendAsync(client, id, limit, chunked, ct);
                Assert.Equal(HttpStatusCode.NoContent, exactReplay.StatusCode);
                await UploadContractTests.FinishAsync(fixture, client, id, ct);
            }
        }
    }

    [Fact]
    public async Task ReferenceIngressCeilingRejectsBeforeApiAndLeavesAttemptRepeatable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var kestrel = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage };
        kestrel.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        using var upstream = kestrel.CreateClient();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.Limits.MaxRequestBodySize = 51L * 1024 * 1024; });
        await using var proxy = builder.Build();
        proxy.Run(async context =>
        {
            if (context.Request.ContentLength > 51L * 1024 * 1024) { context.Response.StatusCode = 413; return; }
            using var forwarded = new HttpRequestMessage(HttpMethod.Put, context.Request.Path.ToString()) { Content = new StreamContent(context.Request.Body) };
            // Preserve early status responses while the API rejects Content-Length before reading the body.
            forwarded.Headers.ExpectContinue = true;
            forwarded.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
            forwarded.Content.Headers.ContentType = new("application/octet-stream"); forwarded.Content.Headers.ContentLength = context.Request.ContentLength;
            using var result = await upstream.SendAsync(forwarded, context.RequestAborted);
            context.Response.StatusCode = (int)result.StatusCode; context.Response.ContentType = result.Content.Headers.ContentType?.ToString();
            await result.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await proxy.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(proxy.Urls.Single()) }; client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var upload = await UploadContractTests.InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
        using (var rejected = await SendAsync(client, id, 51L * 1024 * 1024 + 1, false, ct))
        { Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode); }
        var status = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("AwaitingContent", status.GetProperty("state").GetString());
        using (var accepted = await UploadContractTests.PutAsync(author, id, [42], ct)) { Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode); }
        var apiLimit = await UploadContractTests.InitAsync(author, 1, ct);
        using var apiRejected = await SendAsync(client, apiLimit.GetProperty("id").GetGuid(), 50L * 1024 * 1024 + 1, false, ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, apiRejected.StatusCode);
        var problem = await apiRejected.Content.ReadFromJsonAsync<JsonElement>(ct); Assert.Equal("request_too_large", problem.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Guid id, long length, bool chunked, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/uploads/{id}/content") { Content = new RepeatedContent(length, chunked) };
        request.Headers.ExpectContinue = true;
        request.Content.Headers.ContentType = new("application/octet-stream");
        return await client.SendAsync(request, ct);
    }

    private sealed class RepeatedContent(long length, bool chunked) : HttpContent
    {
        protected override bool TryComputeLength(out long result) { result = length; return !chunked; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => WriteAsync(stream, ct);
        private async Task WriteAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            for (long written = 0; written < length;)
            {
                var count = (int)Math.Min(buffer.Length, length - written);
                await stream.WriteAsync(buffer.AsMemory(0, count), ct); written += count;
            }
        }
    }
}
