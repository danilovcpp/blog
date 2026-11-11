using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace BlogEngine.IntegrationTests.Media;

public sealed class PublicationMediaReadTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    internal static byte[] Image(int width = 3, int height = 2)
    {
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(new SKColor(10, 20, 30, 100));
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    internal static async Task<Guid> ImageAsync(PublishingFixture fixture, HttpClient author, byte[] bytes, CancellationToken ct, string name = "исходник.png")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
        { Content = JsonContent.Create(new { purpose = "PublicationImage", name, declaredSize = bytes.Length, declaredContentType = "image/png" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var upload = await PublishingFixture.JsonAsync(await author.SendAsync(request, ct), HttpStatusCode.Created, ct);
        var id = upload.GetProperty("id").GetGuid();
        using var put = await UploadContractTests.PutAsync(author, id, bytes, ct); Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        return (await UploadContractTests.FinishAsync(fixture, author, id, ct)).GetProperty("fileId").GetGuid();
    }

    [Fact]
    public async Task PublicationAttachmentAccessTracksPublicationStateWithoutCountingViews()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct); using var other = await fixture.ActorAsync(LocalRole.Author, ct);
        using var moderator = await fixture.ActorAsync(LocalRole.Moderator, ct);
        var file = await PublicationMediaWriteTests.AttachmentAsync(fixture, author, ct);
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        { title = "read", slug = "read", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { file } }, ct), HttpStatusCode.Created, ct);
        var id = publication.GetProperty("id").GetGuid(); var path = $"/api/v1/publications/{id}/media/{file}/content?variant=attachment";
        foreach (var client in new[] { fixture.Anonymous, reader, other, moderator })
        { using var denied = await client.GetAsync(path, ct); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); }
        foreach (var client in new[] { author, admin })
        { using var allowed = await client.GetAsync(path, ct); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); }
        publication = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        foreach (var client in new[] { fixture.Anonymous, reader, other, moderator, author, admin })
        {
            using var response = await client.GetAsync(path, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        }
        var detail = await PublishingFixture.JsonAsync(await fixture.Anonymous.GetAsync($"/api/v1/publications/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal(0, detail.GetProperty("viewCount").GetInt64()); Assert.Single(detail.GetProperty("attachments").EnumerateArray());
        _ = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/unpublish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        using var hidden = await fixture.Anonymous.GetAsync(path, ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task ImagesFitWithoutUpscaleKeepAlphaAndTenPrivateSourcesSurviveRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var corpus = new Dictionary<Guid, (byte[] Hash, string Name)>();
        for (var i = 0; i < 10; i++)
        {
            var bytes = Image(i == 0 ? 3000 : 3 + i, i == 0 ? 1500 : 2); var name = $"оригинал-{i}.png";
            var file = await ImageAsync(fixture, author, bytes, ct, name); corpus.Add(file, (SHA256.HashData(bytes), name));
            using var display = await author.GetAsync($"/api/v1/media/files/{file}/content?variant=display", ct);
            Assert.Equal(HttpStatusCode.OK, display.StatusCode); Assert.Equal("image/png", display.Content.Headers.ContentType!.MediaType);
            using var decoded = SKBitmap.Decode(await display.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(i == 0 ? 2560 : 3 + i, decoded.Width); Assert.Equal(i == 0 ? 1280 : 2, decoded.Height);
            Assert.Equal(100, decoded.GetPixel(0, 0).Alpha);
            using var hidden = await fixture.Anonymous.GetAsync($"/api/v1/media/files/{file}/content?variant=original", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, hidden.StatusCode);
        }
        await VerifyAsync(author);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await fixture.Factory.DisposeAsync(); await garage.RestartAsync(ct);
        await using var restarted = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage };
        using var client = restarted.CreateClient(); client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        await VerifyAsync(client);
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("media/phase4/private-sources.json"),
            JsonSerializer.Serialize(corpus.Select(p => new { FileId = p.Key, Sha256 = Convert.ToHexString(p.Value.Hash) })), ct);

        async Task VerifyAsync(HttpClient client)
        {
            foreach (var (file, expected) in corpus)
            {
                using var response = await client.GetAsync($"/api/v1/media/files/{file}/content?variant=original", ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(expected.Name, response.Content.Headers.ContentDisposition!.FileNameStar);
                Assert.Equal(expected.Hash, SHA256.HashData(await response.Content.ReadAsByteArrayAsync(ct)));
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DownloadRechecksAccessAndAdminRoleBeforeSuccessOrStorageError(bool missing, bool revokeAdmin)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        var file = await PublicationMediaWriteTests.AttachmentAsync(fixture, author, ct);
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        { title = "protected", slug = "protected", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { file } }, ct), HttpStatusCode.Created, ct);
        var id = publication.GetProperty("id").GetGuid();
        if (!revokeAdmin)
        { publication = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct); }
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var source = await db.MediaObjects.SingleAsync(o => o.FileId == file && o.Kind == MediaObjectKind.Source, ct);
        if (missing) { using var s3 = garage.CreateClient(); await s3.DeleteObjectAsync(garage.OriginalsBucket, source.ObjectKey, ct); }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var downloading = new ApiFactory(connection, fixture.Identity)
        {
            MediaStorage = garage,
            CustomizeServices = services =>
            {
                services.RemoveAll<IMediaObjectStorage>();
                services.AddSingleton<IMediaObjectStorage>(p => new GatedStorage(ActivatorUtilities.CreateInstance<GarageObjectStorage>(p), entered, release));
            }
        };
        using var client = downloading.CreateClient();
        if (revokeAdmin) { client.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization; }
        var responseTask = client.GetAsync($"/api/v1/publications/{id}/media/{file}/content?variant=attachment", ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (revokeAdmin) { await db.Database.ExecuteSqlRawAsync("DELETE FROM accounts.role_assignments WHERE role=3", ct); }
            else
            { using var unpublish = await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/unpublish", publication.GetProperty("version").GetGuid(), null, ct); Assert.Equal(HttpStatusCode.OK, unpublish.StatusCode); }
        }
        finally { release.TrySetResult(); }
        using var response = await responseTask; Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain("same.bin", body, StringComparison.Ordinal);
        Assert.DoesNotContain("protected", body, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task OneImageCanServeTwoPublicationsWithoutLeakingOriginalOrOtherUsages()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var image = await ImageAsync(fixture, author, Image(), ct);
        var publications = new List<JsonElement>();
        for (var i = 0; i < 2; i++)
        {
            var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
            { title = $"image {i}", slug = $"image-{i}", markdown = "![legacy](https://example.test/image.png)", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverFileId = image, inlineImageIds = new[] { image } }, ct), HttpStatusCode.Created, ct);
            var id = publication.GetProperty("id").GetGuid();
            publications.Add(await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct));
        }
        var first = publications[0]; var id1 = first.GetProperty("id").GetGuid(); var id2 = publications[1].GetProperty("id").GetGuid();
        var path1 = first.GetProperty("coverUrl").GetString()!; var path2 = publications[1].GetProperty("coverUrl").GetString()!;
        using (var allowed = await fixture.Anonymous.GetAsync(path1, ct)) { Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); }
        foreach (var variant in new[] { "attachment", "original" })
        {
            using var denied = await fixture.Anonymous.GetAsync($"/api/v1/publications/{id1}/media/{image}/content?variant={variant}", ct);
            Assert.Equal(variant == "original" ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, denied.StatusCode);
        }
        using (var invalidBearer = new HttpRequestMessage(HttpMethod.Get, path1))
        {
            invalidBearer.Headers.Authorization = new("Bearer", "invalid");
            using var denied = await fixture.Anonymous.SendAsync(invalidBearer, ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        foreach (var (accept, expected) in new[] { ("image/png", HttpStatusCode.OK), ("application/octet-stream", HttpStatusCode.NotAcceptable), ("application/problem+json", HttpStatusCode.NotAcceptable), ("text/html", HttpStatusCode.NotAcceptable) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path1); request.Headers.Accept.Add(new(accept));
            using var response = await fixture.Anonymous.SendAsync(request, ct); Assert.Equal(expected, response.StatusCode);
        }
        var detail = await PublishingFixture.JsonAsync(await fixture.Anonymous.GetAsync($"/api/v1/publications/{id1}", ct), HttpStatusCode.OK, ct);
        Assert.DoesNotContain("ownerId", detail.GetRawText(), StringComparison.Ordinal); Assert.DoesNotContain(id2.ToString("D"), detail.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(3, detail.GetProperty("inlineImages")[0].GetProperty("width").GetInt32());
        var feed = await PublishingFixture.JsonAsync(await fixture.Anonymous.GetAsync("/api/v1/publications", ct), HttpStatusCode.OK, ct);
        Assert.All(feed.GetProperty("items").EnumerateArray(), p => { Assert.Equal(image, p.GetProperty("coverFileId").GetGuid()); Assert.False(p.TryGetProperty("inlineImages", out _)); });
        using (var deleted = await PublishingFixture.SendAsync(author, HttpMethod.Delete, $"/api/v1/publications/{id1}", first.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        clock.Now = clock.Now.AddSeconds(61);
        using (var hidden = await fixture.Anonymous.GetAsync(path1, ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        using (var stillVisible = await fixture.Anonymous.GetAsync(path2, ct)) { Assert.Equal(HttpStatusCode.OK, stillVisible.StatusCode); }
        using var inUse = await author.DeleteAsync($"/api/v1/media/files/{image}", ct); Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
    }

    [Fact]
    public async Task ImageRoleStateAndVariantMatrixProtectsOriginalAndDraft()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct); using var other = await fixture.ActorAsync(LocalRole.Author, ct);
        using var moderator = await fixture.ActorAsync(LocalRole.Moderator, ct);
        var file = await ImageAsync(fixture, author, Image(), ct);
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        { title = "matrix", slug = "matrix", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverFileId = file, inlineImageIds = new[] { file } }, ct), HttpStatusCode.Created, ct);
        var id = publication.GetProperty("id").GetGuid();
        foreach (var published in new[] { false, true })
        {
            if (published) { publication = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct); }
            foreach (var (client, privileged, guest) in new[] { (fixture.Anonymous, false, true), (reader, false, false), (other, false, false), (moderator, false, false), (author, true, false), (admin, true, false) })
            {
                using var display = await client.GetAsync($"/api/v1/publications/{id}/media/{file}/content?variant=display", ct);
                Assert.Equal(published || privileged ? HttpStatusCode.OK : HttpStatusCode.NotFound, display.StatusCode);
                using var attachment = await client.GetAsync($"/api/v1/publications/{id}/media/{file}/content?variant=attachment", ct);
                Assert.Equal(HttpStatusCode.NotFound, attachment.StatusCode);
                foreach (var variant in new[] { "original", "display" })
                {
                    using var download = await client.GetAsync($"/api/v1/media/files/{file}/content?variant={variant}", ct);
                    Assert.Equal(guest ? HttpStatusCode.Unauthorized : privileged ? HttpStatusCode.OK : HttpStatusCode.NotFound, download.StatusCode);
                }
            }
        }
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var owner = publication.GetProperty("author").GetProperty("id").GetGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct);
        using var retained = await author.GetAsync($"/api/v1/media/files/{file}/content?variant=display", ct); Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
    }

    [Fact]
    public async Task InvalidImageIsRejectedWithoutReadyBytesOrQuota()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var bytes = "<svg>invalid content</svg>"u8.ToArray();
        using var init = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
        { Content = JsonContent.Create(new { purpose = "PublicationImage", name = "bad.png", declaredSize = bytes.Length, declaredContentType = "image/png" }) };
        init.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var upload = await PublishingFixture.JsonAsync(await author.SendAsync(init, ct), HttpStatusCode.Created, ct); var id = upload.GetProperty("id").GetGuid();
        using var put = await UploadContractTests.PutAsync(author, id, bytes, ct); Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        using var complete = await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct); Assert.Equal(HttpStatusCode.Accepted, complete.StatusCode);
        using (var scope = fixture.Factory.Services.CreateScope()) { await scope.ServiceProvider.GetRequiredService<BlogEngine.Core.Media.Application.UploadCompletion>().ProcessAsync(id, ct); }
        var status = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("Rejected", status.GetProperty("state").GetString()); Assert.Equal("validation_failed", status.GetProperty("errorCode").GetString());
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        Assert.Equal(0, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes); Assert.Empty(await db.MediaRepresentations.ToArrayAsync(ct));
        Assert.Equal(MediaFileState.Deleted, (await db.MediaFiles.SingleAsync(ct)).State); Assert.Single(await db.MediaCleanupJobs.ToArrayAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetailReadsTextAndLinksFromOneSnapshot(bool workspace)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new ViewCommandGate(sql => sql.Contains("media.representations", StringComparison.Ordinal));
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: gate, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var firstFile = await PublicationMediaWriteTests.AttachmentAsync(fixture, author, ct);
        var secondFile = await PublicationMediaWriteTests.AttachmentAsync(fixture, author, ct);
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        { title = "before", slug = "snapshot", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { firstFile } }, ct), HttpStatusCode.Created, ct);
        var id = publication.GetProperty("id").GetGuid();
        publication = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        gate.Arm();
        var reading = workspace ? author.GetAsync($"/api/v1/workspace/publications/{id}", ct) : fixture.Anonymous.GetAsync($"/api/v1/publications/{id}", ct);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            using var edited = await PublishingFixture.SendAsync(author, HttpMethod.Put, $"/api/v1/publications/{id}", publication.GetProperty("version").GetGuid(), new
            { title = "after", slug = "snapshot", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { secondFile } }, ct);
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        var snapshot = await PublishingFixture.JsonAsync(await reading, HttpStatusCode.OK, ct);
        Assert.Equal("before", snapshot.GetProperty("title").GetString()); Assert.Equal(firstFile, snapshot.GetProperty("attachments")[0].GetProperty("fileId").GetGuid());
    }

    [Fact]
    public async Task DetailUsesBoundedProjectionAndIndexedMediaLookup()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: capture, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var file = await PublicationMediaWriteTests.AttachmentAsync(fixture, author, ct);
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        { title = "bounded", slug = "bounded", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { file } }, ct), HttpStatusCode.Created, ct);
        var id = publication.GetProperty("id").GetGuid();
        publication = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        capture.Clear(); using var detail = await fixture.Anonymous.GetAsync($"/api/v1/publications/{id}", ct); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var queries = capture.Commands.Where(c => c.Sql.StartsWith("SELECT", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, queries.Length);
        var media = Assert.Single(queries, c => c.Sql.Contains("media.representations", StringComparison.Ordinal));
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        await db.Database.OpenConnectionAsync(ct);
        await using var explain = db.Database.GetDbConnection().CreateCommand(); explain.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + media.Sql;
        foreach (var parameter in media.Parameters) { explain.Parameters.Add(new Npgsql.NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value }); }
        var plan = (string)(await explain.ExecuteScalarAsync(ct))!;
        Assert.Contains("publication_usages", plan, StringComparison.Ordinal);
        using var planDocument = JsonDocument.Parse(plan);
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("media/phase4/detail-query.json"), JsonSerializer.Serialize(new { DataQueries = queries.Length, Sql = queries.Select(q => q.Sql), Explain = planDocument.RootElement }), ct);
    }

    private sealed class GatedStorage(IMediaObjectStorage inner, TaskCompletionSource entered, TaskCompletionSource release) : IMediaObjectStorage, IDisposable
    {
        public Task PutAsync(MediaBucketKind bucket, string key, IMediaSpoolFile content, CancellationToken ct) => inner.PutAsync(bucket, key, content, ct);
        public Task DeleteAsync(MediaBucketKind bucket, string key, CancellationToken ct) => inner.DeleteAsync(bucket, key, ct);
        public async Task<IMediaSpoolFile> GetVerifiedAsync(MediaBucketKind bucket, string key, long length, ReadOnlyMemory<byte> hash, CancellationToken ct)
        {
            try { return await inner.GetVerifiedAsync(bucket, key, length, hash, ct); }
            finally { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        }
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }
}
