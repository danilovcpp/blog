using System.Net;
using System.Net.Http.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Publishing;

[Trait("Category", "Publishing")]
[Trait("Category", "ConsumerContract")]
public sealed class TaxonomyContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("categories", 20)]
    [InlineData("tags", 100)]
    public async Task TaxonomyPagesUseStableNameAndIdOrderWithoutDuplicates(string kind, int pageSize)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var expected = new List<(Guid Id, string Name)>();
        for (var i = 0; i < 250; i++)
        {
            var name = i % 2 == 0 ? "A" : "a";
            if (kind == "categories") { var item = new Category(name, "item-" + i); db.Categories.Add(item); expected.Add((item.Id, item.Name)); }
            else { var item = new Tag(name, "item-" + i); db.Tags.Add(item); expected.Add((item.Id, item.Name)); }
        }
        await db.SaveChangesAsync(ct);
        var actual = new List<Guid>();
        for (var page = 1; ; page++)
        {
            var response = await JsonAsync(await f.Anonymous.GetAsync($"/api/v1/{kind}?page={page}&pageSize={pageSize}", ct), HttpStatusCode.OK, ct);
            var items = response.GetProperty("items").EnumerateArray().ToArray(); Assert.InRange(items.Length, 1, pageSize);
            actual.AddRange(items.Select(x => x.GetProperty("id").GetGuid()));
            if (!response.GetProperty("hasNextPage").GetBoolean()) { break; }
        }
        Assert.Equal(expected.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).Select(x => x.Id), actual);
        Assert.Equal(250, actual.Distinct().Count());
    }

    [Theory]
    [InlineData("categories")]
    [InlineData("tags")]
    public async Task AdminLifecycleAndAssignmentsUseVersionAndRestrictDeletion(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        var admin = await f.ActorAsync(LocalRole.Admin, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var root = "/api/v1/" + kind;
        using (var denied = await author.PostAsJsonAsync(root, new { name = "N", slug = "same" }, ct)) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var item = await JsonAsync(await admin.PostAsJsonAsync(root, new { name = " Name\u0085", slug = "same" }, ct), HttpStatusCode.Created, ct);
        var id = item.GetProperty("id").GetGuid(); var version = item.GetProperty("version").GetGuid(); var path = root + "/" + id;
        var read = await JsonAsync(await f.Anonymous.GetAsync(path, ct), HttpStatusCode.OK, ct); Assert.Equal(item.GetRawText(), read.GetRawText());
        Assert.Equal("Name", item.GetProperty("name").GetString());
        using (var duplicate = await admin.PostAsJsonAsync(root, new { name = "Other", slug = "same" }, ct)) { Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode); }
        await JsonAsync(await admin.PostAsJsonAsync(kind == "categories" ? "/api/v1/tags" : "/api/v1/categories", new { name = "Other", slug = "same" }, ct), HttpStatusCode.Created, ct);
        var draft = await JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", Content(categories: kind == "categories" ? [id] : [], tags: kind == "tags" ? [id] : []), ct), HttpStatusCode.Created, ct);
        var pubId = draft.GetProperty("id").GetGuid(); var pubVersion = draft.GetProperty("version").GetGuid();
        var renamed = await JsonAsync(await SendAsync(admin, HttpMethod.Put, path, version, new { name = "Renamed" }, ct), HttpStatusCode.OK, ct);
        using (var stale = await SendAsync(admin, HttpMethod.Delete, path, version, null, ct)) { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        var conflict = await JsonAsync(await SendAsync(admin, HttpMethod.Delete, path, renamed.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.Conflict, ct);
        Assert.Equal("taxonomy_in_use", conflict.GetProperty("code").GetString());
        var published = await JsonAsync(await SendAsync(author, HttpMethod.Post, "/api/v1/publications/" + pubId + "/publish", pubVersion, null, ct), HttpStatusCode.OK, ct);
        var detail = await JsonAsync(await f.Anonymous.GetAsync("/api/v1/publications/" + pubId, ct), HttpStatusCode.OK, ct);
        Assert.Equal("Renamed", detail.GetProperty(kind)[0].GetProperty("name").GetString());
        using (var removed = await SendAsync(author, HttpMethod.Delete, "/api/v1/publications/" + pubId, published.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode); }
        using (var removed = await SendAsync(admin, HttpMethod.Delete, path, renamed.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode); }
        var page = await JsonAsync(await f.Anonymous.GetAsync(root + "?page=2147483647&pageSize=100", ct), HttpStatusCode.OK, ct);
        Assert.Empty(page.GetProperty("items").EnumerateArray());
        using var cache = await f.Anonymous.GetAsync(root, ct);
        Assert.True(cache.Headers.CacheControl?.Private); Assert.True(cache.Headers.CacheControl?.NoStore); Assert.Contains("Authorization", cache.Headers.Vary);
    }
}
