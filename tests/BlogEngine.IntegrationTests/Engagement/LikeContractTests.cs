using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Http;
using BlogEngine.IntegrationTests.Reading;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Engagement;

[Trait("Category", "Likes")]
[Trait("Category", "ConsumerContract")]
public sealed class LikeContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    internal static string PathFor(Guid id) => $"/api/v1/publications/{id}/like";
    internal static Task<HttpResponseMessage> LikeAsync(HttpClient client, Guid id, bool desired, CancellationToken ct) =>
        SendAsync(client, desired ? HttpMethod.Put : HttpMethod.Delete, PathFor(id), null, null, ct);

    [Theory]
    [InlineData(LocalRole.Reader, false)]
    [InlineData(LocalRole.Author, false)]
    [InlineData(LocalRole.Moderator, false)]
    [InlineData(LocalRole.Moderator, true)]
    [InlineData(LocalRole.Admin, false)]
    public async Task EveryRoleCanOnlyChangeOwnLikeAndContentDatesAndVersionRemainUsable(LocalRole role, bool addAuthor)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var reader = await f.ActorAsync(role, ct);
        var pub = await PublishedAsync(author, "like-roles", ct); var id = pub.GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        if (addAuthor)
        {
            var me = await JsonAsync(await reader.GetAsync("/api/v1/me", ct), HttpStatusCode.OK, ct);
            db.RoleAssignments.Add(new(me.GetProperty("id").GetGuid(), LocalRole.Author, DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct);
        }
        var before = await db.Publications.AsNoTracking().SingleAsync(ct);
        var readerContext = await JsonAsync(await reader.GetAsync("/api/v1/me", ct), HttpStatusCode.OK, ct);
        var readerId = readerContext.GetProperty("id").GetGuid();
        foreach (var client in new[] { reader, author })
        {
            foreach (var own in new[] { false, true })
            {
                // A publication may belong to a Reader after Author is revoked; ownership grants no extra reaction rights.
                var ownerId = (client == reader) == own ? readerId : before.AuthorId;
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET author_id={ownerId} WHERE id={id}", ct);
                foreach (var desired in new[] { false, true, true, false, false, true })
                {
                    using var response = await LikeAsync(client, id, desired, ct);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    Assert.Equal(3, result.EnumerateObject().Count()); Assert.Equal(desired, result.GetProperty("isLikedByCurrentUser").GetBoolean());
                    Assert.Equal(await db.Likes.LongCountAsync(ct), result.GetProperty("likeCount").GetInt64()); Assert.Equal(0, result.GetProperty("viewCount").GetInt64());
                    var after = await db.Publications.AsNoTracking().SingleAsync(ct);
                    Assert.Equal(before.Version, after.Version); Assert.Equal(before.UpdatedAt, after.UpdatedAt);
                    Assert.Equal(before.FirstPublishedAt, after.FirstPublishedAt); Assert.Equal(before.Markdown, after.Markdown);
                }
            }
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET author_id={before.AuthorId} WHERE id={id}", ct);
        // The original content version still authorizes a subsequent content transition.
        using var close = await SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/unpublish", before.Version, null, ct);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        foreach (var deleted in new[] { false, true })
        {
            if (deleted) { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET deleted_at=now() WHERE id={id}", ct); }
            foreach (var client in new[] { author, reader })
            {
                foreach (var desired in new[] { true, false })
                { using var denied = await LikeAsync(client, id, desired, ct); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); }
            }
        }
    }

    [Fact]
    public async Task StrictBodylessContractAndParentGuardDoNotLeakReactions()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(author, "like-guard", ct); var id = pub.GetProperty("id").GetGuid();
        foreach (var desired in new[] { true, false })
        {
            using (var anonymous = await LikeAsync(f.Anonymous, id, desired, ct)) { await InfrastructureContractTests.AssertProblem(anonymous, 401, "authentication_required"); }
            using (var body = await SendAsync(author, desired ? HttpMethod.Put : HttpMethod.Delete, PathFor(id), null, new { userId = Guid.NewGuid() }, ct))
            { await InfrastructureContractTests.AssertProblem(body, 400, "invalid_request"); }
            using (var invalid = await SendAsync(author, desired ? HttpMethod.Put : HttpMethod.Delete, "/api/v1/publications/bad/like", null, null, ct))
            { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        }
        // No resource version is required or consumed by the desired-state resource.
        using (var request = new HttpRequestMessage(HttpMethod.Put, PathFor(id)))
        {
            request.Headers.Add("X-Resource-Version", "irrelevant"); using var response = await author.SendAsync(request, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var publicList = await f.Anonymous.GetAsync($"/api/v1/publications/{id}/likes", ct)) { Assert.Equal(HttpStatusCode.NotFound, publicList.StatusCode); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        foreach (var state in new[] { "draft", "deleted", "absent" })
        {
            var target = state == "absent" ? Guid.NewGuid() : id;
            if (state == "draft") { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET state=0 WHERE id={id}", ct); }
            if (state == "deleted") { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET state=1,deleted_at=now() WHERE id={id}", ct); }
            foreach (var client in new[] { author, admin })
            {
                foreach (var desired in new[] { true, false })
                { using var denied = await LikeAsync(client, target, desired, ct); await InfrastructureContractTests.AssertProblem(denied, 404, "resource_not_found"); }
                using var counters = await client.GetAsync($"/api/v1/publications/{target}/counters", ct); Assert.Equal(HttpStatusCode.NotFound, counters.StatusCode);
            }
        }
        Assert.Equal(1, await db.Likes.CountAsync(ct)); Assert.Equal(1, (await db.Publications.SingleAsync(ct)).LikeCount);
    }

    [Fact]
    public async Task FeedDetailAndCountersIsolatePersonalStateAndInvalidBearerFailsClosed()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var reader = await f.ActorAsync(LocalRole.Reader, ct); var other = await f.ActorAsync(LocalRole.Reader, ct);
        var pub = await PublishedAsync(author, "like-personal", ct); var id = pub.GetProperty("id").GetGuid();
        await JsonAsync(await LikeAsync(reader, id, true, ct), HttpStatusCode.OK, ct);
        foreach (var client in new[] { reader, other, f.Anonymous })
        {
            foreach (var path in new[] { "/api/v1/publications", $"/api/v1/publications/{id}", "/api/v1/publications/by-slug/like-personal", $"/api/v1/publications/{id}/counters" })
            {
                using var response = await client.GetAsync(path, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore); Assert.Contains("Authorization", response.Headers.Vary);
                var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (result.TryGetProperty("items", out var items)) { result = Assert.Single(items.EnumerateArray()); }
                Assert.Equal(1, result.GetProperty("likeCount").GetInt64()); Assert.Equal(client == reader, result.GetProperty("isLikedByCurrentUser").GetBoolean());
            }
        }
        f.Anonymous.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Get })
        {
            using var response = await SendAsync(f.Anonymous, method, method == HttpMethod.Get ? $"/api/v1/publications/{id}/counters" : PathFor(id), null, null, ct);
            await InfrastructureContractTests.AssertProblem(response, 401, "invalid_token");
        }
    }

    [Fact]
    public async Task PopularityReflectsWritesWithAnyCategoriesAllTagsAndStableTies()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture();
        await using var f = await StartAsync(postgres, ct, capture); await ReadingDataset.SeedAsync(f, ct);
        ReadingDataset.Authenticate(f.Anonymous, f);
        foreach (var id in new[] { ReadingDataset.Id(2), ReadingDataset.Id(3), ReadingDataset.Id(6), ReadingDataset.Id(12) })
        { await JsonAsync(await LikeAsync(f.Anonymous, id, false, ct), HttpStatusCode.OK, ct); }
        await JsonAsync(await LikeAsync(f.Anonymous, ReadingDataset.Id(1), true, ct), HttpStatusCode.OK, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        foreach (var filters in new[] { (Categories: Array.Empty<int>(), Tags: Array.Empty<int>()), (Categories: new[] { 0, 1 }, Tags: new[] { 0, 1 }) })
        {
            var candidates = ReadingDataset.Expected("popular", filters.Categories, filters.Tags);
            var expected = await db.Publications.AsNoTracking().Where(p => candidates.Contains(p.Id)).OrderByDescending(p => p.LikeCount).ThenByDescending(p => p.FirstPublishedAt).ThenByDescending(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
            var seen = new List<Guid>();
            for (var page = 1; ; page++)
            {
                var result = await JsonAsync(await f.Anonymous.GetAsync(ReadingDataset.Query("popular", page, 20, filters.Categories, filters.Tags), ct), HttpStatusCode.OK, ct);
                seen.AddRange(result.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
                if (!result.GetProperty("hasNextPage").GetBoolean()) { break; }
            }
            Assert.Equal(expected, seen);
        }
        await db.Database.ExecuteSqlRawAsync("ANALYZE publishing.publications; ANALYZE engagement.likes", ct);
        var index = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname='publishing' AND indexname='ix_publications_popular'").SingleAsync(ct);
        Assert.Contains("like_count DESC, first_published_at DESC, id DESC", index, StringComparison.Ordinal);
        var evidence = new List<object>();
        foreach (var path in new[] { ReadingDataset.Query("popular"), ReadingDataset.Query("popular", categories: [0, 1], tags: [0, 1]), "/api/v1/publications/" + ReadingDataset.Id(2), $"/api/v1/publications/{ReadingDataset.Id(2)}/counters" })
        {
            capture.Clear(); await JsonAsync(await f.Anonymous.GetAsync(path, ct), HttpStatusCode.OK, ct);
            var data = capture.Commands.Where(c => !c.IsAuthentication).ToArray(); Assert.InRange(data.Length, 1, 5);
            Assert.Contains(data, c => c.Sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) && c.Sql.Contains("engagement.likes", StringComparison.Ordinal));
            await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct);
            var plans = new List<object>();
            foreach (var command in data)
            {
                await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.Sql, connection);
                foreach (var p in command.Parameters) { explain.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value }); }
                plans.Add(new { sql = command.Sql, plan = JsonSerializer.Deserialize<JsonElement>((string)(await explain.ExecuteScalarAsync(ct))!) });
            }
            evidence.Add(new { path, dataQueries = data.Length, authQueries = capture.Commands.Count(c => c.IsAuthentication), plans });
        }
        await File.WriteAllTextAsync(ArtifactPath("queries/us6-reads.json"), JsonSerializer.Serialize(new { index, evidence }), ct);
    }

    [Fact]
    public async Task OpenApiDescribesBodylessReaderCommandsAndExactCounters()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var document = await JsonAsync(await f.Anonymous.GetAsync("/openapi/v1.json", ct), HttpStatusCode.OK, ct);
        foreach (var method in new[] { "put", "delete" })
        {
            var operation = document.GetProperty("paths").GetProperty("/api/v1/publications/{id}/like").GetProperty(method);
            Assert.False(operation.TryGetProperty("requestBody", out _)); Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            Assert.DoesNotContain(operation.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "X-Resource-Version");
            foreach (var status in new[] { "200", "400", "401", "403", "404", "500", "503" }) { Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _)); }
        }
        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("Counters");
        Assert.Equal(3, schema.GetProperty("required").GetArrayLength()); Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }
}
