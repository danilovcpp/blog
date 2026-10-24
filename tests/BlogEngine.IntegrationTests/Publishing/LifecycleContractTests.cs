using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Publishing;

[Trait("Category", "Publishing")]
[Trait("Category", "ConsumerContract")]
public sealed class LifecycleContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task IndependentLifecycleUsesRuntimeCredentialAndKeepsPrivateAndPublicContractsSeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct);
        var other = await f.ActorAsync(LocalRole.Author, ct);
        var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var moderator = await f.ActorAsync(LocalRole.Moderator, ct);
        using var creation = await author.PostAsJsonAsync("/api/v1/publications", Content(markdown: ""), ct);
        Assert.Equal("no-store", creation.Headers.CacheControl?.ToString());
        Assert.StartsWith("/api/v1/workspace/publications/", creation.Headers.Location?.OriginalString);
        var draft = await JsonAsync(creation, HttpStatusCode.Created, ct);
        var id = draft.GetProperty("id").GetGuid(); var path = "/api/v1/publications/" + id;
        Guid Version(JsonElement json) => json.GetProperty("version").GetGuid();
        Assert.Equal("Draft", draft.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, draft.GetProperty("firstPublishedAt").ValueKind);
        foreach (var client in new[] { f.Anonymous, author, other, admin })
        { using var hidden = await client.GetAsync(path, ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        foreach (var client in new[] { other, moderator })
        { using var hidden = await client.GetAsync("/api/v1/workspace/publications/" + id, ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        using (var foreign = await SendAsync(other, HttpMethod.Post, path + "/publish", null, null, ct)) { Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); }
        using (var missing = await SendAsync(author, HttpMethod.Post, path + "/publish", null, null, ct)) { Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode); }
        using (var blank = await SendAsync(author, HttpMethod.Post, path + "/publish", Version(draft), null, ct)) { Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode); }
        var edited = await JsonAsync(await SendAsync(author, HttpMethod.Put, path, Version(draft), Content(), ct), HttpStatusCode.OK, ct);
        var published = await JsonAsync(await SendAsync(author, HttpMethod.Post, path + "/publish", Version(edited), null, ct), HttpStatusCode.OK, ct);
        var first = published.GetProperty("firstPublishedAt").GetString();
        var noOp = await JsonAsync(await SendAsync(author, HttpMethod.Post, path + "/publish", Version(published), null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(Version(published), Version(noOp)); Assert.Equal(published.GetProperty("updatedAt").GetString(), noOp.GetProperty("updatedAt").GetString());
        using (var stale = await SendAsync(author, HttpMethod.Post, path + "/publish", Version(edited), null, ct)) { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        using (var forbidden = await SendAsync(other, HttpMethod.Put, path, null, Content(), ct)) { Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode); }
        var publicDetail = await JsonAsync(await f.Anonymous.GetAsync(path, ct), HttpStatusCode.OK, ct);
        var bySlug = await JsonAsync(await f.Anonymous.GetAsync("/api/v1/publications/by-slug/hello", ct), HttpStatusCode.OK, ct);
        Assert.Equal(publicDetail.GetRawText(), bySlug.GetRawText());
        Assert.False(publicDetail.TryGetProperty("version", out _)); Assert.False(publicDetail.TryGetProperty("state", out _));
        Assert.Single(publicDetail.GetProperty("author").EnumerateObject()); Assert.Equal(0, publicDetail.GetProperty("viewCount").GetInt64());
        var changed = await JsonAsync(await SendAsync(author, HttpMethod.Put, path, Version(published), Content(markdown: "changed"), ct), HttpStatusCode.OK, ct);
        Assert.Equal("changed", (await JsonAsync(await f.Anonymous.GetAsync(path, ct), HttpStatusCode.OK, ct)).GetProperty("markdown").GetString());
        using (var slug = await SendAsync(author, HttpMethod.Put, path, Version(changed), Content(slug: "new"), ct)) { Assert.Equal(HttpStatusCode.Conflict, slug.StatusCode); }
        var unpublish = await JsonAsync(await SendAsync(author, HttpMethod.Post, path + "/unpublish", Version(changed), null, ct), HttpStatusCode.OK, ct);
        var unpublishNoOp = await JsonAsync(await SendAsync(author, HttpMethod.Post, path + "/unpublish", Version(unpublish), null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(Version(unpublish), Version(unpublishNoOp));
        var republish = await JsonAsync(await SendAsync(author, HttpMethod.Post, path + "/publish", Version(unpublish), null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(first, republish.GetProperty("firstPublishedAt").GetString());
        using (var deleted = await SendAsync(admin, HttpMethod.Delete, path, Version(republish), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        using (var deletedAgain = await SendAsync(admin, HttpMethod.Delete, path, Version(republish), null, ct)) { Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode); }
        foreach (var route in new[] { path, "/api/v1/publications/by-slug/hello", "/api/v1/workspace/publications/" + id })
        { using var hidden = await admin.GetAsync(route, ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        var reserved = await JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", Content(), ct), HttpStatusCode.Conflict, ct);
        Assert.Equal("slug_conflict", reserved.GetProperty("code").GetString());
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var tombstone = await db.Publications.SingleAsync(ct);
        Assert.NotNull(tombstone.DeletedAt); Assert.Equal(0, tombstone.ViewCount); Assert.Equal(0, tombstone.LikeCount);
        await File.WriteAllTextAsync(ArtifactPath("us1.json"), JsonSerializer.Serialize(new { status = "passed", runtimeCredential = true, lifecycle = true, tombstone = true, publicPrivateDtos = true }), ct);
    }

    [Fact]
    public async Task WorkspaceIsBoundedAndUnknownSystemFieldsAndBadOptionalBearerAreRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct);
        var reader = await f.ActorAsync(LocalRole.Reader, ct);
        using (var denied = await reader.PostAsJsonAsync("/api/v1/publications", Content(), ct)) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var created = await JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        {
            title = "Title",
            slug = "hello",
            markdown = "body",
            categoryIds = Array.Empty<Guid>(),
            tagIds = Array.Empty<Guid>(),
            description = "Description",
            coverUrl = "https://example.test/image"
        }, ct), HttpStatusCode.Created, ct);
        var id = created.GetProperty("id").GetGuid();
        var cleared = await JsonAsync(await SendAsync(author, HttpMethod.Put, "/api/v1/publications/" + id, created.GetProperty("version").GetGuid(), Content(), ct), HttpStatusCode.OK, ct);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("description").ValueKind); Assert.Equal(JsonValueKind.Null, cleared.GetProperty("coverUrl").ValueKind);
        foreach (var field in new[] { "authorId", "owner", "createdAt", "updatedAt", "state", "likeCount", "viewCount", "version", "firstPublishedAt", "deletedAt" })
        {
            var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Content(slug: Guid.NewGuid().ToString("N"))))!;
            body[field] = "forged";
            using var response = await author.PostAsJsonAsync("/api/v1/publications", body, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var body in new[] { "{}", "{\"title\":null}", "{\"title\":\"a\",\"slug\":\"a\",\"markdown\":\"\",\"categoryIds\":null,\"tagIds\":[]}" })
        { using var response = await author.PostAsync("/api/v1/publications", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        var list = await JsonAsync(await author.GetAsync("/api/v1/workspace/publications?state=Draft&pageSize=1", ct), HttpStatusCode.OK, ct);
        Assert.Single(list.GetProperty("items").EnumerateArray()); Assert.False(list.GetProperty("items")[0].TryGetProperty("markdown", out _));
        var far = await JsonAsync(await author.GetAsync("/api/v1/workspace/publications?page=2147483647&pageSize=100", ct), HttpStatusCode.OK, ct);
        Assert.Empty(far.GetProperty("items").EnumerateArray());
        foreach (var query in new[] { "page=0", "pageSize=101", "state=0", "state=published", "page=1&page=2" })
        { using var invalid = await author.GetAsync("/api/v1/workspace/publications?" + query, ct); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        f.Anonymous.DefaultRequestHeaders.Authorization = new("Bearer", "bad");
        using var badToken = await f.Anonymous.GetAsync("/api/v1/publications/" + id, ct); Assert.Equal(HttpStatusCode.Unauthorized, badToken.StatusCode);
    }
    [Fact]
    public async Task AnonymousCreationIsChallengedAndPublicDraftIsHidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(database.ConnectionString, identity);
        using var client = factory.CreateClient();
        using var create = await client.PostAsJsonAsync("/api/v1/publications", new { title = "Title", slug = "hello", markdown = "", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>() }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        using var hidden = await client.GetAsync("/api/v1/publications/" + Guid.NewGuid(), ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("resource_not_found", (await hidden.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
        Assert.True(hidden.Headers.CacheControl?.Private); Assert.True(hidden.Headers.CacheControl?.NoStore);
    }
}
