using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Discussions;

[Trait("Category", "Discussions")]
[Trait("Category", "ConsumerContract")]
public sealed class DiscussionContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    internal static async Task<JsonElement> PublishedAsync(HttpClient author, string slug, CancellationToken ct)
    {
        var draft = await JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", Content(slug), ct), HttpStatusCode.Created, ct);
        return await JsonAsync(await SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{draft.GetProperty("id").GetGuid()}/publish", draft.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
    }
    internal static async Task<JsonElement> CreateAsync(HttpClient client, Guid pub, Guid? parent, CancellationToken ct, string content = " comment ") =>
        await JsonAsync(await client.PostAsJsonAsync($"/api/v1/publications/{pub}/comments", new { content, parentId = parent }, ct), HttpStatusCode.Created, ct);

    [Fact]
    public async Task ReaderLifecyclePreservesRepliesAndPrivateVersionsAndNeverCountsGet()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var reader = await f.ActorAsync(LocalRole.Reader, ct); var other = await f.ActorAsync(LocalRole.Reader, ct);
        var pub = await PublishedAsync(author, "discussion", ct); var pubId = pub.GetProperty("id").GetGuid(); var path = $"/api/v1/publications/{pubId}/comments";
        using (var denied = await f.Anonymous.PostAsJsonAsync(path, new { content = "x" }, ct)) { Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        var root = await CreateAsync(reader, pubId, null, ct); var id = root.GetProperty("id").GetGuid(); var version = root.GetProperty("version").GetGuid(); var editPath = $"/api/v1/comments/{id}";
        var reply = await CreateAsync(other, pubId, id, ct, "<script>untrusted</script>");
        using (var denied = await SendAsync(author, HttpMethod.Put, editPath, null, new { content = "stolen" }, ct)) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var denied = await other.GetAsync(editPath, ct)) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var edited = await JsonAsync(await SendAsync(reader, HttpMethod.Put, editPath, version, new { content = " edited " }, ct), HttpStatusCode.OK, ct);
        Assert.Equal("edited", edited.GetProperty("content").GetString()); Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        using (var stale = await SendAsync(reader, HttpMethod.Delete, editPath, version, null, ct)) { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        using (var deleted = await SendAsync(reader, HttpMethod.Delete, editPath, edited.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        var context = await JsonAsync(await reader.GetAsync(editPath, ct), HttpStatusCode.OK, ct);
        using (var repeated = await SendAsync(reader, HttpMethod.Delete, editPath, context.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode); }
        Assert.Equal(context.GetRawText(), (await JsonAsync(await reader.GetAsync(editPath, ct), HttpStatusCode.OK, ct)).GetRawText());
        using var response = await f.Anonymous.GetAsync(path, ct); Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore); Assert.Contains("Authorization", response.Headers.Vary);
        var roots = await response.Content.ReadFromJsonAsync<JsonElement>(ct); var placeholder = Assert.Single(roots.GetProperty("items").EnumerateArray());
        Assert.Equal("Deleted", placeholder.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("content").ValueKind); Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("author").ValueKind);
        Assert.True(placeholder.GetProperty("hasReplies").GetBoolean()); Assert.False(placeholder.TryGetProperty("version", out _));
        var children = await JsonAsync(await f.Anonymous.GetAsync(path + "?parentId=" + id, ct), HttpStatusCode.OK, ct);
        Assert.Equal(reply.GetProperty("id").GetGuid(), Assert.Single(children.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using (var invalid = await reader.PostAsJsonAsync(path, new { content = "x", parentId = id }, ct)) { Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); var p = await db.Publications.SingleAsync(ct);
        Assert.Equal(pub.GetProperty("version").GetGuid(), p.Version); Assert.Equal(0, p.ViewCount);
    }

    [Fact]
    public async Task HiddenContextRoleMatrixAndParentGuardsAreAppliedBeforeVersion()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var owner = await f.ActorAsync(LocalRole.Reader, ct); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(author, "guards", ct); var pubId = pub.GetProperty("id").GetGuid(); var root = await CreateAsync(owner, pubId, null, ct); var id = root.GetProperty("id").GetGuid(); var path = $"/api/v1/comments/{id}";
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE discussions.comments SET state=1 WHERE id={id}", ct);
        var hidden = await JsonAsync(await owner.GetAsync(path, ct), HttpStatusCode.OK, ct); Assert.Equal(JsonValueKind.Null, hidden.GetProperty("content").ValueKind);
        var retained = await JsonAsync(await admin.GetAsync(path, ct), HttpStatusCode.OK, ct); Assert.Equal("comment", retained.GetProperty("content").GetString());
        using (var denied = await SendAsync(owner, HttpMethod.Put, path, null, new { content = "x" }, ct)) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var adminEdit = await JsonAsync(await SendAsync(admin, HttpMethod.Put, path, hidden.GetProperty("version").GetGuid(), new { content = "retained" }, ct), HttpStatusCode.OK, ct); Assert.Equal("Hidden", adminEdit.GetProperty("state").GetString());
        using (var contextResponse = await owner.GetAsync(path, ct)) { Assert.True(contextResponse.Headers.CacheControl!.NoStore); }
        foreach (var role in new[] { LocalRole.Reader, LocalRole.Author, LocalRole.Moderator })
        {
            var stranger = await f.ActorAsync(role, ct);
            using var get = await stranger.GetAsync(path, ct); Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
            using var delete = await SendAsync(stranger, HttpMethod.Delete, path, Guid.NewGuid(), null, ct); Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
            await CreateAsync(stranger, pubId, null, ct);
        }
        using (var inaccessible = await f.Anonymous.GetAsync($"/api/v1/publications/{pubId}/comments?parentId={id}", ct)) { Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode); }
        using (var inaccessible = await owner.PostAsJsonAsync($"/api/v1/publications/{pubId}/comments", new { content = "x", parentId = id }, ct)) { Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode); }
        using (var deleted = await SendAsync(owner, HttpMethod.Delete, path, adminEdit.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        var otherPub = await PublishedAsync(author, "foreign", ct);
        using (var foreign = await owner.PostAsJsonAsync($"/api/v1/publications/{otherPub.GetProperty("id").GetGuid()}/comments", new { content = "x", parentId = id }, ct)) { Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); }
        using (var closed = await SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{pubId}/unpublish", pub.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.OK, closed.StatusCode); }
        using (var unavailable = await admin.GetAsync(path, ct)) { Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode); }
        using (var unavailable = await SendAsync(admin, HttpMethod.Delete, path, null, null, ct)) { Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode); }
    }

    [Fact]
    public async Task InvalidInputsDepthAndOptionalBearerHaveStableErrors()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "inputs", ct); var pubId = pub.GetProperty("id").GetGuid(); var path = $"/api/v1/publications/{pubId}/comments";
        var root = await CreateAsync(author, pubId, null, ct); var id = root.GetProperty("id").GetGuid(); var parent = id;
        for (var i = 2; i <= 5; i++) { parent = (await CreateAsync(author, pubId, parent, ct)).GetProperty("id").GetGuid(); }
        using (var deep = await author.PostAsJsonAsync(path, new { content = "x", parentId = parent }, ct)) { Assert.Equal(HttpStatusCode.Conflict, deep.StatusCode); }
        foreach (var body in new object[] { new { content = " " }, new { content = "x\0" }, new { content = new string('x', 5001) }, new { content = "x", authorId = Guid.NewGuid() }, new { content = "x", state = "Hidden" }, new { parentId = id } })
        { using var invalid = await author.PostAsJsonAsync(path, body, ct); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        foreach (var query in new[] { "?page=0", "?pageSize=101", "?parentId=x", "?parentId=", "?parentId=" + id.ToString("N"), "?state=Hidden", "?parentId=" + id + "&parentId=" + id })
        { using var invalid = await f.Anonymous.GetAsync(path + query, ct); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using (var missingVersion = await SendAsync(author, HttpMethod.Put, $"/api/v1/comments/{id}", null, new { content = "x" }, ct)) { Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode); }
        using (var body = await SendAsync(author, HttpMethod.Delete, $"/api/v1/comments/{id}", root.GetProperty("version").GetGuid(), new { }, ct)) { Assert.Equal(HttpStatusCode.BadRequest, body.StatusCode); }
        using var badBearer = new HttpRequestMessage(HttpMethod.Get, path); badBearer.Headers.Authorization = new("Bearer", "bad");
        using var denied = await f.Anonymous.SendAsync(badBearer, ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        foreach (var property in new[] { "parentId", "authorId", "publicationId", "version", "createdAt", "state" })
        {
            using var reassignment = await SendAsync(author, HttpMethod.Put, $"/api/v1/comments/{id}", root.GetProperty("version").GetGuid(), new Dictionary<string, object> { ["content"] = "x", [property] = Guid.NewGuid() }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, reassignment.StatusCode);
        }
        using var malformedVersion = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/comments/{id}"); malformedVersion.Headers.Add("X-Resource-Version", "bad");
        using var malformed = await author.SendAsync(malformedVersion, ct); Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task OpenApiDescribesSeparateVisiblePlaceholderAndPrivateForms()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var document = await JsonAsync(await f.Anonymous.GetAsync("/openapi/v1.json", ct), HttpStatusCode.OK, ct);
        await File.WriteAllTextAsync(ArtifactPath("us4-openapi.json"), document.GetRawText(), ct);
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var publicSchema = schemas.GetProperty("PublicCommentDto");
        Assert.False(publicSchema.TryGetProperty("discriminator", out _));
        Assert.Equal(2, publicSchema.GetProperty("oneOf").GetArrayLength());
        foreach (var reference in publicSchema.GetProperty("oneOf").EnumerateArray())
        {
            var name = reference.GetProperty("$ref").GetString()!.Split('/')[^1];
            var shape = schemas.GetProperty(name); var properties = shape.GetProperty("properties");
            Assert.False(properties.TryGetProperty("version", out _)); Assert.Contains("state", shape.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
            if (name.EndsWith("CommentPlaceholderDto", StringComparison.Ordinal))
            { Assert.Equal("null", properties.GetProperty("content").GetProperty("type").GetString()); Assert.Equal("null", properties.GetProperty("author").GetProperty("type").GetString()); }
        }
        Assert.True(schemas.GetProperty("CommentEditContextDto").GetProperty("properties").TryGetProperty("version", out _));
        Assert.Equal(3, schemas.GetProperty("CommentEditContextDto").GetProperty("oneOf").GetArrayLength());
        var post = document.GetProperty("paths").GetProperty("/api/v1/publications/{id}/comments").GetProperty("post");
        Assert.True(post.GetProperty("responses").GetProperty("201").TryGetProperty("content", out _));
    }

    [Fact]
    public async Task HiddenPlaceholdersNeverDiscloseContentOrAuthorAndDisappearAfterLastVisibleDescendant()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var owner = await f.ActorAsync(LocalRole.Reader, ct); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(author, "hidden-tree", ct); var pubId = pub.GetProperty("id").GetGuid(); var path = $"/api/v1/publications/{pubId}/comments";
        var root = await CreateAsync(owner, pubId, null, ct, "private-hidden-root"); var rootId = root.GetProperty("id").GetGuid();
        var child = await CreateAsync(owner, pubId, rootId, ct, "private-hidden-child"); var childId = child.GetProperty("id").GetGuid();
        var leaf = await CreateAsync(author, pubId, childId, ct); var leafId = leaf.GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        // Fixture-only moderation keeps the same ancestor invariant as the future moderation service.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE discussions.comments SET state=1 WHERE id={rootId} OR id={childId}; UPDATE discussions.comments SET visible_descendant_count=visible_descendant_count-1 WHERE id={rootId}", ct);
        foreach (var client in new[] { f.Anonymous, owner, admin })
        {
            var roots = await JsonAsync(await client.GetAsync(path, ct), HttpStatusCode.OK, ct); var placeholder = Assert.Single(roots.GetProperty("items").EnumerateArray());
            Assert.Equal("Hidden", placeholder.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("content").ValueKind); Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("author").ValueKind); Assert.True(placeholder.GetProperty("hasReplies").GetBoolean());
        }
        var children = await JsonAsync(await f.Anonymous.GetAsync(path + "?parentId=" + rootId, ct), HttpStatusCode.OK, ct);
        Assert.Equal(childId, Assert.Single(children.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using (var invalid = await owner.PostAsJsonAsync(path, new { content = "x", parentId = rootId }, ct)) { Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode); }
        using (var deleted = await SendAsync(owner, HttpMethod.Delete, $"/api/v1/comments/{childId}", child.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        await CommentConcurrencyTests.AssertCountsAsync(db, ct);
        using (var deleted = await SendAsync(author, HttpMethod.Delete, $"/api/v1/comments/{leafId}", leaf.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        await CommentConcurrencyTests.AssertCountsAsync(db, ct);
        var empty = await JsonAsync(await f.Anonymous.GetAsync(path, ct), HttpStatusCode.OK, ct); Assert.Empty(empty.GetProperty("items").EnumerateArray());
        using var missing = await f.Anonymous.GetAsync(path + "?parentId=" + rootId, ct); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain(f.Factory.Logs.Entries, e => e.Message.Contains("private-hidden-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Author")]
    [InlineData("Moderator")]
    [InlineData("AuthorModerator")]
    [InlineData("Admin")]
    public async Task RoleOwnershipAndStateMatrixIsEnforcedThroughHttp(string roles)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var actor = await f.ActorAsync(roles == "AuthorModerator" ? LocalRole.Moderator : Enum.Parse<LocalRole>(roles), ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        if (roles == "AuthorModerator")
        {
            var me = await JsonAsync(await actor.GetAsync("/api/v1/me", ct), HttpStatusCode.OK, ct);
            db.RoleAssignments.Add(new(me.GetProperty("id").GetGuid(), LocalRole.Author, DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct);
        }
        var pub = await PublishedAsync(author, "matrix", ct); var pubId = pub.GetProperty("id").GetGuid();
        foreach (var own in new[] { false, true })
        {
            foreach (var state in new[] { "Visible", "Hidden", "Deleted" })
            {
                var comment = await CreateAsync(own ? actor : author, pubId, null, ct); var id = comment.GetProperty("id").GetGuid(); var version = comment.GetProperty("version").GetGuid(); var path = $"/api/v1/comments/{id}";
                if (state != "Visible")
                { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE discussions.comments SET state={(short)(state == "Hidden" ? 1 : 2)},text=CASE WHEN {state}='Deleted' THEN NULL ELSE text END WHERE id={id}", ct); }
                var allowed = own || roles == "Admin";
                using (var get = await actor.GetAsync(path, ct))
                {
                    Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, get.StatusCode);
                    if (allowed)
                    {
                        var context = await get.Content.ReadFromJsonAsync<JsonElement>(ct);
                        Assert.Equal(state == "Deleted" || (state == "Hidden" && roles != "Admin") ? JsonValueKind.Null : JsonValueKind.String, context.GetProperty("content").ValueKind);
                    }
                }
                using (var edit = await SendAsync(actor, HttpMethod.Put, path, version, new { content = "edited" }, ct))
                {
                    var expected = !allowed || (state == "Hidden" && roles != "Admin") ? HttpStatusCode.Forbidden : state == "Deleted" ? HttpStatusCode.Conflict : HttpStatusCode.OK;
                    Assert.Equal(expected, edit.StatusCode);
                    if (expected == HttpStatusCode.OK) { version = (await edit.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("version").GetGuid(); }
                }
                using var delete = await SendAsync(actor, HttpMethod.Delete, path, version, null, ct);
                Assert.Equal(allowed ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, delete.StatusCode);
            }
        }
        var root = await CreateAsync(author, pubId, null, ct); var rootPath = $"/api/v1/comments/{root.GetProperty("id").GetGuid()}";
        using (var denied = await f.Anonymous.GetAsync(rootPath, ct)) { Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        using (var denied = await SendAsync(f.Anonymous, HttpMethod.Put, rootPath, root.GetProperty("version").GetGuid(), new { content = "x" }, ct)) { Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        using (var denied = await SendAsync(f.Anonymous, HttpMethod.Delete, rootPath, root.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
    }
}
