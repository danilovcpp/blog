using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Reading;

[Trait("Category", "Reading")]
[Trait("Category", "ConsumerContract")]
public sealed class ReadingContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    internal static readonly string[] CardFields = ["id", "title", "slug", "description", "coverUrl", "author", "createdAt", "updatedAt", "firstPublishedAt", "categories", "tags", "likeCount", "viewCount", "isLikedByCurrentUser"];

    [Fact]
    public async Task IndependentReadingKeepsDtosPersonalizationAndStoredCountersConsistent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        await ReadingDataset.SeedAsync(f, ct);
        using var first = f.Factory.CreateClient(); using var second = f.Factory.CreateClient();
        ReadingDataset.Authenticate(first, f); ReadingDataset.Authenticate(second, f, second: true);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var before = await db.Publications.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Version, p.ViewCount, p.LikeCount }).ToArrayAsync(ct);
        foreach (var (client, divisor) in new[] { (f.Anonymous, 0), (first, 2), (second, 3) })
        {
            using var response = await client.GetAsync(ReadingDataset.Query(pageSize: 100), ct);
            AssertPrivate(response);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertFields(page, ["items", "page", "pageSize", "hasNextPage"]);
            foreach (var card in page.GetProperty("items").EnumerateArray())
            {
                AssertFields(card, CardFields); AssertFields(card.GetProperty("author"), ["id"]);
                Assert.Equal(ReadingDataset.AuthorId, card.GetProperty("author").GetProperty("id").GetGuid());
                foreach (var date in new[] { "createdAt", "updatedAt", "firstPublishedAt" }) { Assert.EndsWith("Z", card.GetProperty(date).GetString()); }
                foreach (var field in new[] { "categories", "tags" })
                { foreach (var item in card.GetProperty(field).EnumerateArray()) { AssertFields(item, ["id", "name", "slug", "version"]); } }
            }
            // 2 is liked only by the first reader; 3 only by the second, 6 by both, 1 by neither.
            foreach (var number in new[] { 1, 2, 3, 6 })
            {
                var path = "/api/v1/publications/" + ReadingDataset.Id(number);
                var detail = await GetAsync(client, path, ct);
                var slug = await GetAsync(client, "/api/v1/publications/by-slug/reading-" + number, ct);
                Assert.Equal(detail.GetRawText(), slug.GetRawText());
                AssertFields(detail, [.. CardFields, "markdown"]);
                var counters = await GetAsync(client, path + "/counters", ct);
                AssertFields(counters, ["likeCount", "viewCount", "isLikedByCurrentUser"]);
                foreach (var field in new[] { "likeCount", "viewCount", "isLikedByCurrentUser" })
                { Assert.Equal(detail.GetProperty(field).GetRawText(), counters.GetProperty(field).GetRawText()); }
                Assert.Equal(divisor != 0 && number % divisor == 0, detail.GetProperty("isLikedByCurrentUser").GetBoolean());
                Assert.Equal(3_000_000_000L + number, counters.GetProperty("viewCount").GetInt64());
                Assert.Equal(ReadingDataset.Likes(number), counters.GetProperty("likeCount").GetInt64());
            }
            foreach (var card in page.GetProperty("items").EnumerateArray())
            {
                var detail = await GetAsync(client, "/api/v1/publications/" + card.GetProperty("id").GetGuid(), ct);
                foreach (var field in CardFields) { Assert.Equal(detail.GetProperty(field).GetRawText(), card.GetProperty(field).GetRawText()); }
            }
        }
        using var owner = f.Factory.CreateClient(); owner.DefaultRequestHeaders.Authorization = new("Bearer", f.Identity.CreateToken(ReadingDataset.Id(1101)));
        var admin = await f.ActorAsync(LocalRole.Admin, ct);
        foreach (var client in new[] { owner, admin })
        {
            foreach (var number in new[] { 11, 13, 999 })
            {
                foreach (var path in new[] { "/api/v1/publications/" + ReadingDataset.Id(number), "/api/v1/publications/" + ReadingDataset.Id(number) + "/counters", "/api/v1/publications/by-slug/reading-" + number })
                {
                    using var hidden = await client.GetAsync(path, ct); AssertPrivate(hidden);
                    Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); await AssertProblemAsync(hidden, "resource_not_found", ct);
                }
            }
        }
        var after = await db.Publications.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Version, p.ViewCount, p.LikeCount }).ToArrayAsync(ct);
        Assert.Equal(before, after);
        await File.WriteAllTextAsync(ArtifactPath("us2.json"), JsonSerializer.Serialize(new
        { status = "passed", dataset = 250, runtimeCredential = true, independentReading = true, twoUsersAndAnonymous = true, privateCache = true, getsDoNotWrite = true, sc006Publications = true, sc005 = "deferred to final phase" }), ct);
    }

    [Fact]
    public async Task DraftDeletedAndMissingAreIndistinguishableAndBadBearerNeverFallsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        await ReadingDataset.SeedAsync(f, ct);
        foreach (var number in new[] { 11, 13, 999 })
        {
            foreach (var path in new[] { "/api/v1/publications/" + ReadingDataset.Id(number), "/api/v1/publications/" + ReadingDataset.Id(number) + "/counters", "/api/v1/publications/by-slug/reading-" + number })
            {
                using var response = await f.Anonymous.GetAsync(path, ct); AssertPrivate(response);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); await AssertProblemAsync(response, "resource_not_found", ct);
            }
        }
        var routes = new[] { "/api/v1/publications", "/api/v1/publications/" + ReadingDataset.Id(2), "/api/v1/publications/by-slug/reading-2", "/api/v1/publications/" + ReadingDataset.Id(2) + "/counters" };
        f.Anonymous.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        foreach (var path in routes)
        {
            using var response = await f.Anonymous.GetAsync(path, ct); AssertPrivate(response);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
            await AssertProblemAsync(response, "invalid_token", ct);
        }
        f.Anonymous.DefaultRequestHeaders.Authorization = new("Bearer", f.Identity.CreateToken());
        f.Identity.SetResponse("{}", HttpStatusCode.ServiceUnavailable);
        foreach (var path in routes)
        {
            using var response = await f.Anonymous.GetAsync(path, ct); AssertPrivate(response);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); await AssertProblemAsync(response, "identity_keys_unavailable", ct);
        }
    }

    internal static void AssertFields(JsonElement value, string[] fields) =>
        Assert.Equal(fields.Order(StringComparer.Ordinal), value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    internal static void AssertPrivate(HttpResponseMessage response)
    { Assert.True(response.Headers.CacheControl?.Private); Assert.True(response.Headers.CacheControl?.NoStore); Assert.Contains("Authorization", response.Headers.Vary); }
    internal static async Task AssertProblemAsync(HttpResponseMessage response, string code, CancellationToken ct)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(code, json.GetProperty("code").GetString()); Assert.False(string.IsNullOrEmpty(json.GetProperty("traceId").GetString()));
    }
    internal static async Task<JsonElement> GetAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct); AssertPrivate(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
