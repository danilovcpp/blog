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
using Npgsql;

namespace BlogEngine.IntegrationTests.Media;

[Collection("Media transfers")]
public sealed class LibraryContractTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    private static BlogDbContext Context(PublishingFixture fixture) => new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
    [Fact]
    public async Task PrivateLibraryReturnsExactBytesAndBoundedDeterministicPages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var other = await fixture.ActorAsync(LocalRole.Author, ct);
        var empty = await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/files", ct), HttpStatusCode.OK, ct);
        Assert.Empty(empty.GetProperty("items").EnumerateArray()); Assert.False(empty.GetProperty("hasNextPage").GetBoolean());
        var bytes = RandomNumberGenerator.GetBytes(8193);
        var upload = await UploadContractTests.InitAsync(author, bytes.Length, ct);
        var id = upload.GetProperty("id").GetGuid(); var fileId = upload.GetProperty("fileId").GetGuid();
        using (var put = await UploadContractTests.PutAsync(author, id, bytes, ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        await UploadContractTests.FinishAsync(fixture, author, id, ct);
        var page = await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/files", ct), HttpStatusCode.OK, ct);
        Assert.Equal(1, page.GetProperty("page").GetInt32()); Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("items")[0].GetProperty("displayUrl").ValueKind);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/files/{fileId}/content");
        request.Headers.Accept.Add(new("application/octet-stream")); request.Headers.Range = new(0, 1);
        using var download = await author.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("same.bin", download.Content.Headers.ContentDisposition.FileNameStar);
        using (var hidden = await other.GetAsync($"/api/v1/media/files/{fileId}", ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        foreach (var query in new[] { "page=0", "page=-1", "pageSize=101", "page=2147483648", "page=x" })
        { using var invalid = await author.GetAsync("/api/v1/media/files?" + query, ct); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        var far = await (await author.GetAsync("/api/v1/media/files?page=2147483647&pageSize=100", ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Empty(far.GetProperty("items").EnumerateArray()); Assert.False(far.GetProperty("hasNextPage").GetBoolean());
        using var display = await author.GetAsync($"/api/v1/media/files/{fileId}/content?variant=display", ct);
        Assert.Equal(HttpStatusCode.NotFound, display.StatusCode);
    }

    [Fact]
    public async Task HundredAttachmentsKeepNamesAndHashesAcrossHostAndGarageRestartAndTwentyFourHours()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        var corpus = new Dictionary<Guid, (string Name, byte[] Hash, long Length)>();
        for (var i = 0; i < 100; i++)
        {
            var length = i == 0 ? 1 : i == 1 ? 50 * 1024 * 1024 : i * 31;
            var bytes = RandomNumberGenerator.GetBytes(length); var name = i % 2 == 0 ? "одинаковое.bin" : $"attachment-{i}.bin";
            var upload = await UploadContractTests.InitAsync(author, length, ct, name: name); var id = upload.GetProperty("id").GetGuid();
            using (var put = await UploadContractTests.PutAsync(author, id, bytes, ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
            var result = await UploadContractTests.FinishAsync(fixture, author, id, ct);
            corpus.Add(result.GetProperty("fileId").GetGuid(), (name, SHA256.HashData(bytes), length));
        }
        await VerifyAsync(author);
        var found = new List<Guid>();
        for (var page = 1; page <= 6; page++)
        {
            var json = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/files?page={page}", ct), HttpStatusCode.OK, ct);
            found.AddRange(json.GetProperty("items").EnumerateArray().Select(f => f.GetProperty("id").GetGuid()));
            Assert.Equal(page < 5, json.GetProperty("hasNextPage").GetBoolean());
        }
        Assert.Equal(corpus.Keys.OrderDescending(), found); Assert.Equal(100, found.Distinct().Count());
        await using var db = Context(fixture); var owner = (await db.MediaFiles.FirstAsync(ct)).OwnerId;
        var adminPage = await PublishingFixture.JsonAsync(await admin.GetAsync($"/api/v1/media/files?ownerId={owner}&pageSize=100", ct), HttpStatusCode.OK, ct);
        Assert.Equal(100, adminPage.GetProperty("items").GetArrayLength());
        using (var denied = await author.GetAsync($"/api/v1/media/files?ownerId={owner}", ct)) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await fixture.Factory.DisposeAsync(); await garage.RestartAsync(ct); clock.Now = clock.Now.AddDays(2);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct);
        await using var restarted = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage, ViewClock = clock };
        using var readerOwner = restarted.CreateClient(); readerOwner.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        await VerifyAsync(readerOwner);
        var evidence = corpus.Select(p => new { FileId = p.Key, p.Value.Length, Sha256 = Convert.ToHexString(p.Value.Hash) });
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("media/phase3/hundred-attachments.json"), JsonSerializer.Serialize(evidence), ct);

        async Task VerifyAsync(HttpClient client)
        {
            foreach (var (id, expected) in corpus)
            {
                using var download = await client.GetAsync($"/api/v1/media/files/{id}/content", ct);
                Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal(expected.Length, download.Content.Headers.ContentLength);
                Assert.Equal(expected.Name, download.Content.Headers.ContentDisposition!.FileNameStar);
                await using var stream = await download.Content.ReadAsStreamAsync(ct);
                Assert.Equal(expected.Hash, await SHA256.HashDataAsync(stream, ct));
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DownloadRechecksDeletionOrAdminRevocationAfterSuccessfulOrFailedGet(bool missing, bool revokeAdmin)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        var upload = await UploadContractTests.InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid(); var fileId = upload.GetProperty("fileId").GetGuid();
        using (var put = await UploadContractTests.PutAsync(author, id, [5], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        await UploadContractTests.FinishAsync(fixture, author, id, ct);
        await using var db = Context(fixture); var source = await db.MediaObjects.SingleAsync(o => o.FileId == fileId && o.State == MediaObjectState.Present, ct);
        if (missing)
        { using var s3 = garage.CreateClient(); await s3.DeleteObjectAsync(garage.OriginalsBucket, source.ObjectKey, ct); }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
        using var client = downloading.CreateClient(); client.DefaultRequestHeaders.Authorization = (revokeAdmin ? admin : author).DefaultRequestHeaders.Authorization;
        var responseTask = client.GetAsync($"/api/v1/media/files/{fileId}/content", ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (revokeAdmin) { await db.Database.ExecuteSqlRawAsync("DELETE FROM accounts.role_assignments WHERE role=3", ct); }
            else { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE media.files SET state=2,original_object_id=NULL,deleted_at=clock_timestamp(),version={Guid.NewGuid()} WHERE id={fileId}", ct); }
        }
        finally { release.TrySetResult(); }
        using var response = await responseTask; Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct); Assert.Equal("resource_not_found", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("same.bin", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAndCorruptReadyObjectsReturnSafe503BeforeHeaders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        foreach (var missing in new[] { false, true })
        {
            var upload = await UploadContractTests.InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid(); var fileId = upload.GetProperty("fileId").GetGuid();
            using (var put = await UploadContractTests.PutAsync(author, id, [5], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
            await UploadContractTests.FinishAsync(fixture, author, id, ct);
            await using var db = Context(fixture); var source = await db.MediaObjects.SingleAsync(o => o.FileId == fileId && o.State == MediaObjectState.Present, ct);
            using var s3 = garage.CreateClient();
            if (missing) { await s3.DeleteObjectAsync(garage.OriginalsBucket, source.ObjectKey, ct); }
            else
            {
                await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
                { BucketName = garage.OriginalsBucket, Key = source.ObjectKey, InputStream = new MemoryStream([9]), UseChunkEncoding = false }, ct);
            }
            using var response = await author.GetAsync($"/api/v1/media/files/{fileId}/content", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.RetryAfter!.Delta > TimeSpan.Zero);
            Assert.Null(response.Content.Headers.ContentDisposition);
            Assert.Equal("dependency_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task PageUsesOneBoundedDataQueryAndReflectsTheCurrentSet()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture(); var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: capture, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var upload = await UploadContractTests.InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
            using var put = await UploadContractTests.PutAsync(author, id, [1], ct); Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            var result = await UploadContractTests.FinishAsync(fixture, author, id, ct); ids.Add(result.GetProperty("fileId").GetGuid());
        }
        capture.Clear();
        var page = await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/files?pageSize=2", ct), HttpStatusCode.OK, ct);
        Assert.Equal(ids.OrderDescending().Take(2), page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        Assert.True(page.GetProperty("hasNextPage").GetBoolean());
        var command = Assert.Single(capture.Commands, c => c.Sql.Contains("media.files", StringComparison.Ordinal));
        Assert.Contains("LIMIT", command.Sql, StringComparison.Ordinal); Assert.Contains("OFFSET", command.Sql, StringComparison.Ordinal);
        Assert.Contains("created_at DESC", command.Sql, StringComparison.Ordinal); Assert.Contains("id DESC", command.Sql, StringComparison.Ordinal);
        await using var db = Context(fixture); var removed = ids.Max();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE media.files SET state=2,original_object_id=NULL,deleted_at=clock_timestamp(),version={Guid.NewGuid()} WHERE id={removed}", ct);
        var changed = await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/files?pageSize=2", ct), HttpStatusCode.OK, ct);
        Assert.Equal(ids.Where(id => id != removed).OrderDescending(), changed.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        Assert.False(changed.GetProperty("hasNextPage").GetBoolean());
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionString); await connection.OpenAsync(ct);
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + command.Sql, connection);
        foreach (var parameter in command.Parameters) { explain.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value }); }
        await using var reader = await explain.ExecuteReaderAsync(ct); var plan = new List<string>();
        while (await reader.ReadAsync(ct)) { plan.Add(reader.GetString(0)); }
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("media/phase3/library-query.json"), JsonSerializer.Serialize(new { command.Sql, DataQueries = 1, Plan = plan }), ct);
    }

    [Fact]
    public async Task PrivateFileRoleAndStateMatrixHidesAllUnavailableResources()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var upload = await UploadContractTests.InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid(); var ready = upload.GetProperty("fileId").GetGuid();
        using (var put = await UploadContractTests.PutAsync(author, id, [8], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        await UploadContractTests.FinishAsync(fixture, author, id, ct);
        var pending = await UploadContractTests.InitAsync(author, 1, ct); var deleted = await UploadContractTests.InitAsync(author, 1, ct);
        using (var cancel = await author.DeleteAsync($"/api/v1/media/uploads/{deleted.GetProperty("id").GetGuid()}", ct)) { Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode); }
        await using var db = Context(fixture); var owner = (await db.MediaFiles.SingleAsync(f => f.Id == ready, ct)).OwnerId;
        var avatar = MediaFile.Create(owner, MediaPurpose.Avatar, "avatar.png", "image/png", 1, DateTimeOffset.UtcNow);
        avatar.Delete(DateTimeOffset.UtcNow); db.MediaFiles.Add(avatar); await db.SaveChangesAsync(ct);
        var hiddenIds = new[] { pending.GetProperty("fileId").GetGuid(), deleted.GetProperty("fileId").GetGuid(), avatar.Id, Guid.NewGuid() };
        foreach (var role in new[] { LocalRole.Reader, LocalRole.Author, LocalRole.Moderator, LocalRole.Admin })
        {
            using var client = await fixture.ActorAsync(role, ct);
            foreach (var suffix in new[] { "", "/content" })
            {
                using var visible = await client.GetAsync($"/api/v1/media/files/{ready}{suffix}", ct);
                Assert.Equal(role == LocalRole.Admin ? HttpStatusCode.OK : HttpStatusCode.NotFound, visible.StatusCode);
                foreach (var hidden in hiddenIds)
                {
                    using var denied = await client.GetAsync($"/api/v1/media/files/{hidden}{suffix}", ct);
                    Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                }
            }
        }
        using var guest = await fixture.Anonymous.GetAsync($"/api/v1/media/files/{ready}/content", ct); Assert.Equal(HttpStatusCode.Unauthorized, guest.StatusCode);
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
