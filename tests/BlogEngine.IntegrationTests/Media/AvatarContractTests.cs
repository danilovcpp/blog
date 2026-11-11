using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Contracts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace BlogEngine.IntegrationTests.Media;

public sealed class AvatarContractTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    internal static async Task<JsonElement> StateAsync(HttpClient client, CancellationToken ct) =>
        await PublishingFixture.JsonAsync(await client.GetAsync("/api/v1/me/avatar", ct), HttpStatusCode.OK, ct);

    internal static async Task<HttpResponseMessage> InitResponseAsync(HttpClient client, Guid version, byte[] bytes, CancellationToken ct,
        Guid? key = null, string type = "image/png")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
        { Content = JsonContent.Create(new { purpose = "Avatar", name = "avatar.png", declaredSize = bytes.Length, declaredContentType = type }) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString("D"));
        request.Headers.Add("X-Resource-Version", version.ToString("D"));
        return await client.SendAsync(request, ct);
    }

    internal static async Task<JsonElement> PrepareAsync(HttpClient client, Guid version, byte[] bytes, CancellationToken ct)
    {
        var upload = await PublishingFixture.JsonAsync(await InitResponseAsync(client, version, bytes, ct), HttpStatusCode.Created, ct);
        using var put = await UploadContractTests.PutAsync(client, upload.GetProperty("id").GetGuid(), bytes, ct);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        return upload;
    }

    internal static async Task<JsonElement> AssignAsync(PublishingFixture fixture, HttpClient client, CancellationToken ct, byte[]? bytes = null)
    {
        var state = await StateAsync(client, ct);
        var upload = await PrepareAsync(client, state.GetProperty("version").GetGuid(), bytes ?? PublicationMediaReadTests.Image(), ct);
        return await UploadContractTests.FinishAsync(fixture, client, upload.GetProperty("id").GetGuid(), ct);
    }

    [Fact]
    public async Task ReaderAvatarCycleIsVersionedPrivateAndClosesOldUrls()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        using var outsider = await fixture.ActorAsync(LocalRole.Reader, ct);
        using (var guest = await fixture.Anonymous.GetAsync("/api/v1/me/avatar", ct)) { Assert.Equal(HttpStatusCode.Unauthorized, guest.StatusCode); }
        var empty = await StateAsync(reader, ct); var initial = empty.GetProperty("version").GetGuid();
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("avatar").ValueKind);
        var noOp = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(reader, HttpMethod.Delete, "/api/v1/me/avatar", initial, null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(empty.GetRawText(), noOp.GetRawText());
        var first = await AssignAsync(fixture, reader, ct); var firstId = first.GetProperty("fileId").GetGuid();
        var state = await StateAsync(reader, ct);
        var contract = await OpenApiCompatibilityTests.ContractAsync(ct);
        ConsumerWalkthroughTests.Validate(contract, contract["components"]!["schemas"]!["AvatarState"]!, JsonNode.Parse(state.GetRawText()), "avatar-state");
        ConsumerWalkthroughTests.Validate(contract, contract["components"]!["schemas"]!["UploadResult"]!, JsonNode.Parse(first.GetRawText()), "avatar-result");
        Assert.Equal(first.GetProperty("appliedAvatarVersion").GetGuid(), state.GetProperty("version").GetGuid());
        foreach (var size in new[] { 256, 512 })
        {
            using var response = await fixture.Anonymous.GetAsync($"/api/v1/avatars/{firstId}/{size}.png", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            var png = await response.Content.ReadAsByteArrayAsync(ct); using var bitmap = SKBitmap.Decode(png);
            Assert.Equal(size, bitmap.Width); Assert.Equal(size, bitmap.Height); Assert.Equal(100, bitmap.GetPixel(size / 2, size / 2).Alpha);
            Assert.DoesNotContain("eXIf", System.Text.Encoding.Latin1.GetString(png), StringComparison.Ordinal);
        }
        using (var stale = await PublishingFixture.SendAsync(reader, HttpMethod.Delete, "/api/v1/me/avatar", initial, null, ct)) { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        using (var foreign = await admin.DeleteAsync($"/api/v1/me/avatar?ownerId={firstId}", ct)) { Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode); }
        var second = await AssignAsync(fixture, reader, ct);
        using (var old = await fixture.Anonymous.GetAsync($"/api/v1/avatars/{firstId}/256.png", ct)) { Assert.Equal(HttpStatusCode.NotFound, old.StatusCode); }
        foreach (var fileId in new[] { firstId, second.GetProperty("fileId").GetGuid() })
        {
            foreach (var client in new[] { reader, admin, outsider })
            {
                using var hidden = await client.DeleteAsync($"/api/v1/media/files/{fileId}", ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
                using var original = await client.GetAsync($"/api/v1/media/files/{fileId}/content?variant=original", ct); Assert.Equal(HttpStatusCode.NotFound, original.StatusCode);
            }
        }
        state = await StateAsync(reader, ct);
        var deleted = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(reader, HttpMethod.Delete, "/api/v1/me/avatar", state.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("avatar").ValueKind);
        var replay = await PublishingFixture.JsonAsync(await reader.PostAsync($"/api/v1/media/uploads/{first.GetProperty("uploadId").GetGuid()}/complete", null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(first.GetRawText(), replay.GetRawText()); Assert.Equal(deleted.GetRawText(), (await StateAsync(reader, ct)).GetRawText());
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public async Task AvatarAcceptsAllSupportedRasterFormatsThroughHttp(SKEncodedImageFormat format, string type)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        var version = (await StateAsync(reader, ct)).GetProperty("version").GetGuid();
        using var image = SKImage.FromEncodedData(PublicationMediaReadTests.Image()); using var encoded = image.Encode(format, 100);
        var bytes = encoded.ToArray();
        var upload = await PublishingFixture.JsonAsync(await InitResponseAsync(reader, version, bytes, ct, type: type), HttpStatusCode.Created, ct);
        var contract = await OpenApiCompatibilityTests.ContractAsync(ct);
        ConsumerWalkthroughTests.Validate(contract, contract["components"]!["schemas"]!["Upload"]!, JsonNode.Parse(upload.GetRawText()), "avatar-upload");
        var id = upload.GetProperty("id").GetGuid();
        using (var put = await UploadContractTests.PutAsync(reader, id, bytes, ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        var result = await UploadContractTests.FinishAsync(fixture, reader, id, ct);
        using var response = await fixture.Anonymous.GetAsync($"/api/v1/avatars/{result.GetProperty("fileId").GetGuid()}/512.png", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task InvalidContainersAndStorageFailureKeepPreviousAssignmentAndReleaseRejectedReserve()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        await AssignAsync(fixture, reader, ct); var previous = await StateAsync(reader, ct);
        var version = previous.GetProperty("version").GetGuid(); var png = PublicationMediaReadTests.Image();
        byte[] animation = [0, 0, 0, 8, 97, 99, 84, 76, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        using var image = SKImage.FromEncodedData(png); using var webpData = image.Encode(SKEncodedImageFormat.Webp, 100);
        var webp = webpData.ToArray().Concat(new byte[] { 65, 78, 73, 77, 0, 0, 0, 0 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4), (uint)webp.Length - 8);
        foreach (var (bytes, type) in new[] { ("<svg/>"u8.ToArray(), "image/png"), (png[..^5], "image/png"),
            (png[..33].Concat(animation).Concat(png[33..]).ToArray(), "image/png"), (webp, "image/webp"), (png, "image/jpeg") })
        {
            var upload = await PublishingFixture.JsonAsync(await InitResponseAsync(reader, version, bytes, ct, type: type), HttpStatusCode.Created, ct);
            var id = upload.GetProperty("id").GetGuid();
            using (var put = await UploadContractTests.PutAsync(reader, id, bytes, ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
            using (var accepted = await reader.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
            using var scope = fixture.Factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, ct);
            var status = await PublishingFixture.JsonAsync(await reader.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
            Assert.Equal("Rejected", status.GetProperty("state").GetString());
            Assert.Equal(previous.GetRawText(), (await StateAsync(reader, ct)).GetRawText());
        }
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var failing = new ApiFactory(connection, fixture.Identity)
        {
            MediaStorage = garage,
            ViewClock = clock,
            CustomizeServices = services =>
            {
                services.RemoveAll<IMediaObjectStorage>();
                services.AddSingleton<IMediaObjectStorage>(p => new ControlledStorage(ActivatorUtilities.CreateInstance<GarageObjectStorage>(p), failRenditionPut: true));
            }
        };
        using var failingClient = failing.CreateClient(); failingClient.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
        var retry = await PrepareAsync(failingClient, version, png, ct); var retryId = retry.GetProperty("id").GetGuid();
        using (var accepted = await failingClient.PostAsync($"/api/v1/media/uploads/{retryId}/complete", null, ct)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        using (var scope = failing.Services.CreateScope()) { await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(retryId, ct); }
        Assert.Equal(previous.GetRawText(), (await StateAsync(reader, ct)).GetRawText());
        var pending = await PublishingFixture.JsonAsync(await reader.GetAsync($"/api/v1/media/uploads/{retryId}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("Processing", pending.GetProperty("state").GetString());
        clock.Now = clock.Now.AddMinutes(16);
        using (var scope = fixture.Factory.Services.CreateScope()) { await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(retryId, ct); }
        var current = await StateAsync(reader, ct); Assert.Equal(retry.GetProperty("fileId").GetGuid(), current.GetProperty("fileId").GetGuid());
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        Assert.Equal(png.Length, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes);
        Assert.Equal(2, await db.MediaRepresentations.CountAsync(ct));
        var source = await db.MediaObjects.SingleAsync(o => o.FileId == current.GetProperty("fileId").GetGuid() && o.Kind == MediaObjectKind.Source, ct);
        Assert.Equal(MediaObjectState.Retired, source.State); Assert.True(await db.MediaCleanupJobs.AnyAsync(j => j.ObjectId == source.Id, ct));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DownloadRechecksAssignmentAfterSuccessfulOrFailedGet(bool missing, bool delete)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        var result = await AssignAsync(fixture, reader, ct); var id = result.GetProperty("fileId").GetGuid();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var obj = await db.MediaObjects.SingleAsync(o => o.FileId == id && o.Kind == MediaObjectKind.Avatar256, ct);
        if (missing) { using var s3 = garage.CreateClient(); await s3.DeleteObjectAsync(garage.RenditionsBucket, obj.ObjectKey, ct); }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var downloading = new ApiFactory(connection, fixture.Identity)
        {
            MediaStorage = garage,
            CustomizeServices = services =>
            {
                services.RemoveAll<IMediaObjectStorage>();
                services.AddSingleton<IMediaObjectStorage>(p => new ControlledStorage(ActivatorUtilities.CreateInstance<GarageObjectStorage>(p), entered, release));
            }
        };
        using var client = downloading.CreateClient(); var responseTask = client.GetAsync($"/api/v1/avatars/{id}/256.png", ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (delete)
            {
                var state = await StateAsync(reader, ct);
                using var deleted = await PublishingFixture.SendAsync(reader, HttpMethod.Delete, "/api/v1/me/avatar", state.GetProperty("version").GetGuid(), null, ct);
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            }
            else { await AssignAsync(fixture, reader, ct); }
        }
        finally { release.TrySetResult(); }
        using var response = await responseTask; Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("resource_not_found", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task VersionBindingBinaryContractAndCurrentMissingObjectAreStrict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        using (var noVersion = await reader.DeleteAsync("/api/v1/me/avatar", ct)) { Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode); }
        var result = await AssignAsync(fixture, reader, ct); var id = result.GetProperty("fileId").GetGuid(); var path = $"/api/v1/avatars/{id}/256.png";
        using (var invalid = new HttpRequestMessage(HttpMethod.Get, path))
        {
            invalid.Headers.Authorization = new("Bearer", "invalid"); using var response = await fixture.Anonymous.SendAsync(invalid, ct); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using (var unacceptable = new HttpRequestMessage(HttpMethod.Get, path))
        {
            unacceptable.Headers.Accept.Add(new("application/octet-stream")); using var response = await fixture.Anonymous.SendAsync(unacceptable, ct); Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        }
        using (var range = new HttpRequestMessage(HttpMethod.Get, path))
        {
            range.Headers.Range = new(0, 1); using var response = await fixture.Anonymous.SendAsync(range, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.False(response.Headers.Contains("Accept-Ranges"));
            Assert.Equal((await response.Content.ReadAsByteArrayAsync(ct)).LongLength, response.Content.Headers.ContentLength);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        }
        using (var wrongSize = await fixture.Anonymous.GetAsync($"/api/v1/avatars/{id}/128.png", ct)) { Assert.Equal(HttpStatusCode.NotFound, wrongSize.StatusCode); }
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var obj = await db.MediaObjects.SingleAsync(o => o.FileId == id && o.Kind == MediaObjectKind.Avatar256, ct);
        using (var s3 = garage.CreateClient()) { await s3.DeleteObjectAsync(garage.RenditionsBucket, obj.ObjectKey, ct); }
        using var missing = await fixture.Anonymous.GetAsync(path, ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.NotNull(missing.Headers.RetryAfter); Assert.Contains("dependency_unavailable", await missing.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    internal sealed class ControlledStorage(IMediaObjectStorage inner, TaskCompletionSource? entered = null, TaskCompletionSource? release = null,
        bool failRenditionPut = false) : IMediaObjectStorage, IDisposable
    {
        public async Task PutAsync(MediaBucketKind bucket, string key, IMediaSpoolFile content, CancellationToken ct)
        {
            await inner.PutAsync(bucket, key, content, ct);
            if (failRenditionPut && bucket == MediaBucketKind.Renditions) { throw new MediaStorageException("media_storage_unavailable", true); }
        }
        public Task DeleteAsync(MediaBucketKind bucket, string key, CancellationToken ct) => inner.DeleteAsync(bucket, key, ct);
        public async Task<IMediaSpoolFile> GetVerifiedAsync(MediaBucketKind bucket, string key, long length, ReadOnlyMemory<byte> hash, CancellationToken ct)
        {
            try { return await inner.GetVerifiedAsync(bucket, key, length, hash, ct); }
            finally { if (entered is not null) { entered.TrySetResult(); await release!.Task.WaitAsync(ct); } }
        }
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }
}
