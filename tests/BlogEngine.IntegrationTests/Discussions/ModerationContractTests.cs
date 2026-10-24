using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Http;
using Microsoft.EntityFrameworkCore;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Discussions;

[Trait("Category", "Moderation")]
[Trait("Category", "ConsumerContract")]
public sealed class ModerationContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    internal const string ListPath = "/api/v1/moderation/comments";
    internal static Task<HttpResponseMessage> TransitionAsync(HttpClient client, JsonElement comment, string action, CancellationToken ct) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/comments/{comment.GetProperty("id").GetGuid()}/{action}", comment.GetProperty("version").GetGuid(), null, ct);

    [Theory]
    [InlineData("Reader")]
    [InlineData("Author")]
    [InlineData("Moderator")]
    [InlineData("AuthorModerator")]
    [InlineData("Admin")]
    public async Task RolesOwnershipStatesAndVersionPrecedenceUseRealBearer(string roles)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct);
        var actor = await f.ActorAsync(roles == "AuthorModerator" ? LocalRole.Moderator : Enum.Parse<LocalRole>(roles), ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        if (roles == "AuthorModerator")
        {
            var me = await JsonAsync(await actor.GetAsync("/api/v1/me", ct), HttpStatusCode.OK, ct);
            db.RoleAssignments.Add(new(me.GetProperty("id").GetGuid(), LocalRole.Author, DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct);
        }
        var pub = await PublishedAsync(author, "moderation-matrix", ct); var pubId = pub.GetProperty("id").GetGuid();
        var allowed = roles is "Moderator" or "AuthorModerator" or "Admin";
        using (var list = await actor.GetAsync(ListPath, ct)) { await AuthorizationMatrix.AssertAsync(list, allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden); }
        foreach (var own in new[] { false, true })
        {
            foreach (var state in new[] { "Visible", "Hidden", "Deleted" })
            {
                var comment = await CreateAsync(own ? actor : author, pubId, null, ct); var id = comment.GetProperty("id").GetGuid();
                if (state != "Visible")
                { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE discussions.comments SET state={(short)(state == "Hidden" ? 1 : 2)},text=CASE WHEN {state}='Deleted' THEN NULL ELSE text END WHERE id={id}", ct); }
                foreach (var action in new[] { "hide", "restore" })
                {
                    using (var missing = await SendAsync(actor, HttpMethod.Post, $"/api/v1/comments/{id}/{action}", null, null, ct))
                    { await InfrastructureContractTests.AssertProblem(missing, allowed ? 400 : 403, allowed ? "validation_failed" : "forbidden"); }
                    using (var stale = await SendAsync(actor, HttpMethod.Post, $"/api/v1/comments/{id}/{action}", Guid.NewGuid(), null, ct))
                    { await AuthorizationMatrix.AssertAsync(stale, allowed ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden); }
                    using var result = await TransitionAsync(actor, comment, action, ct);
                    if (!allowed) { await AuthorizationMatrix.AssertAsync(result, HttpStatusCode.Forbidden); }
                    else if (state == "Deleted") { await InfrastructureContractTests.AssertProblem(result, 409, "invalid_state"); }
                    else
                    {
                        Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.True(result.Headers.CacheControl!.NoStore);
                        comment = await result.Content.ReadFromJsonAsync<JsonElement>(ct);
                        Assert.Equal(action == "hide" ? "Hidden" : "Visible", comment.GetProperty("state").GetString());
                        Assert.Equal("comment", comment.GetProperty("content").GetString()); Assert.NotEqual(JsonValueKind.Null, comment.GetProperty("author").ValueKind);
                    }
                }
            }
        }
        var root = await CreateAsync(author, pubId, null, ct);
        using (var anonymous = await f.Anonymous.GetAsync(ListPath, ct)) { await AuthorizationMatrix.AssertAsync(anonymous, HttpStatusCode.Unauthorized); }
        foreach (var action in new[] { "hide", "restore" })
        { using var anonymous = await TransitionAsync(f.Anonymous, root, action, ct); await AuthorizationMatrix.AssertAsync(anonymous, HttpStatusCode.Unauthorized); }
        using (var closed = await SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{pubId}/unpublish", pub.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.OK, closed.StatusCode); }
        foreach (var action in new[] { "hide", "restore" })
        { using var closed = await SendAsync(actor, HttpMethod.Post, $"/api/v1/comments/{root.GetProperty("id").GetGuid()}/{action}", null, null, ct); await AuthorizationMatrix.AssertAsync(closed, HttpStatusCode.NotFound); }
    }

    [Fact]
    public async Task HiddenTreeOwnerDeletionAdminEditAndTerminalStateDoNotLeakText()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var owner = await f.ActorAsync(LocalRole.Reader, ct);
        var moderator = await f.ActorAsync(LocalRole.Moderator, ct); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(author, "moderated-tree", ct); var pubId = pub.GetProperty("id").GetGuid();
        var root = await CreateAsync(owner, pubId, null, ct, "private-moderation-marker"); var id = root.GetProperty("id").GetGuid();
        await CreateAsync(author, pubId, id, ct); var path = $"/api/v1/comments/{id}";
        using (var denied = await TransitionAsync(author, root, "hide", ct)) { await AuthorizationMatrix.AssertAsync(denied, HttpStatusCode.Forbidden); }
        var hidden = await JsonAsync(await TransitionAsync(moderator, root, "hide", ct), HttpStatusCode.OK, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var before = await db.Comments.AsNoTracking().SingleAsync(c => c.Id == id, ct);
        var repeated = await JsonAsync(await TransitionAsync(moderator, hidden, "hide", ct), HttpStatusCode.OK, ct);
        Assert.Equal(hidden.GetRawText(), repeated.GetRawText());
        var after = await db.Comments.AsNoTracking().SingleAsync(c => c.Id == id, ct);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt); Assert.Equal(before.EditedAt, after.EditedAt);
        Assert.Equal(before.Version, after.Version); Assert.Equal(before.VisibleDescendantCount, after.VisibleDescendantCount);
        var context = await JsonAsync(await owner.GetAsync(path, ct), HttpStatusCode.OK, ct); Assert.Equal(JsonValueKind.Null, context.GetProperty("content").ValueKind);
        using (var denied = await SendAsync(owner, HttpMethod.Put, path, hidden.GetProperty("version").GetGuid(), new { content = "owner edit" }, ct)) { await AuthorizationMatrix.AssertAsync(denied, HttpStatusCode.Forbidden); }
        using (var denied = await SendAsync(moderator, HttpMethod.Put, path, hidden.GetProperty("version").GetGuid(), new { content = "moderator edit" }, ct)) { await AuthorizationMatrix.AssertAsync(denied, HttpStatusCode.Forbidden); }
        using (var denied = await SendAsync(moderator, HttpMethod.Delete, path, hidden.GetProperty("version").GetGuid(), null, ct)) { await AuthorizationMatrix.AssertAsync(denied, HttpStatusCode.Forbidden); }
        var edited = await JsonAsync(await SendAsync(admin, HttpMethod.Put, path, hidden.GetProperty("version").GetGuid(), new { content = "private-admin-marker" }, ct), HttpStatusCode.OK, ct);
        Assert.Equal("Hidden", edited.GetProperty("state").GetString());
        foreach (var client in new[] { f.Anonymous, owner, moderator, admin })
        {
            var tree = await JsonAsync(await client.GetAsync($"/api/v1/publications/{pubId}/comments", ct), HttpStatusCode.OK, ct);
            var placeholder = Assert.Single(tree.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("content").ValueKind); Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("author").ValueKind);
            Assert.False(placeholder.TryGetProperty("version", out _)); Assert.DoesNotContain("private-", tree.GetRawText(), StringComparison.Ordinal);
        }
        var restored = await JsonAsync(await TransitionAsync(moderator, edited, "restore", ct), HttpStatusCode.OK, ct);
        Assert.Equal("private-admin-marker", restored.GetProperty("content").GetString());
        hidden = await JsonAsync(await TransitionAsync(moderator, restored, "hide", ct), HttpStatusCode.OK, ct);
        using (var deleted = await SendAsync(owner, HttpMethod.Delete, path, hidden.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        var deletedContext = await JsonAsync(await admin.GetAsync(path, ct), HttpStatusCode.OK, ct);
        foreach (var action in new[] { "hide", "restore" })
        { using var terminal = await TransitionAsync(moderator, deletedContext, action, ct); await InfrastructureContractTests.AssertProblem(terminal, 409, "invalid_state"); }
        var list = await JsonAsync(await moderator.GetAsync(ListPath + "?state=Deleted", ct), HttpStatusCode.OK, ct);
        Assert.Equal(JsonValueKind.Null, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("content").ValueKind);
        await CommentConcurrencyTests.AssertCountsAsync(db, ct);
        Assert.DoesNotContain(f.Factory.Logs.Entries, e => e.Message.Contains("private-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListHasExactFiltersStablePaginationAndPublishedGuardAndStrictInput()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var moderator = await f.ActorAsync(LocalRole.Moderator, ct);
        var pub = await PublishedAsync(author, "moderation-pages", ct); var pubId = pub.GetProperty("id").GetGuid();
        var otherPub = await PublishedAsync(author, "moderation-other", ct); var otherId = otherPub.GetProperty("id").GetGuid();
        var comments = new List<JsonElement>();
        for (var i = 0; i < 25; i++) { comments.Add(await CreateAsync(author, pubId, null, ct)); }
        await CreateAsync(author, otherId, null, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        await db.Database.ExecuteSqlRawAsync("UPDATE discussions.comments SET created_at=TIMESTAMPTZ '2026-10-24 00:00:00Z',updated_at=TIMESTAMPTZ '2026-10-24 00:00:00Z'", ct);
        await JsonAsync(await TransitionAsync(moderator, comments[0], "hide", ct), HttpStatusCode.OK, ct);
        using (var deleted = await SendAsync(author, HttpMethod.Delete, $"/api/v1/comments/{comments[1].GetProperty("id").GetGuid()}", comments[1].GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        foreach (var state in new[] { "", "Visible", "Hidden", "Deleted" })
        {
            var expected = await db.Comments.AsNoTracking().Where(c => c.PublicationId == pubId && (state == "" || c.State.ToString() == state)).OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => c.Id).ToListAsync(ct);
            var seen = new List<Guid>(); var number = 1;
            while (true)
            {
                var page = await JsonAsync(await moderator.GetAsync($"{ListPath}?publicationId={pubId}&pageSize=7&page={number}" + (state == "" ? "" : "&state=" + state), ct), HttpStatusCode.OK, ct);
                seen.AddRange(page.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
                if (!page.GetProperty("hasNextPage").GetBoolean()) { break; }
                number++;
            }
            Assert.Equal(expected, seen);
        }
        var distant = await JsonAsync(await moderator.GetAsync(ListPath + "?page=2147483647&pageSize=100", ct), HttpStatusCode.OK, ct); Assert.Empty(distant.GetProperty("items").EnumerateArray());
        foreach (var query in new[] { "?state=hidden", "?state=0", "?state=", "?state=Visible&state=Hidden", "?publicationId=x", "?publicationId=", "?publicationId=" + pubId.ToString("N"), "?publicationId=" + pubId + "&publicationId=" + pubId, "?page=0", "?pageSize=101", "?unknown=1" })
        { using var invalid = await moderator.GetAsync(ListPath + query, ct); await InfrastructureContractTests.AssertProblem(invalid, 400, "validation_failed"); }
        using (var body = await SendAsync(moderator, HttpMethod.Post, $"/api/v1/comments/{comments[0].GetProperty("id").GetGuid()}/hide", comments[0].GetProperty("version").GetGuid(), new { }, ct)) { await InfrastructureContractTests.AssertProblem(body, 400, "invalid_request"); }
        foreach (var action in new[] { "hide", "restore" })
        {
            using var malformed = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/comments/{comments[0].GetProperty("id").GetGuid()}/{action}");
            malformed.Headers.Add("X-Resource-Version", "bad");
            using var invalid = await moderator.SendAsync(malformed, ct); await InfrastructureContractTests.AssertProblem(invalid, 400, "validation_failed");
        }
        foreach (var parent in new[] { pubId, otherId })
        {
            if (parent == pubId) { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET state=0 WHERE id={parent}", ct); }
            else { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET deleted_at=now() WHERE id={parent}", ct); }
            using var closed = await moderator.GetAsync(ListPath + "?publicationId=" + parent, ct); await AuthorizationMatrix.AssertAsync(closed, HttpStatusCode.NotFound);
        }
        using (var absent = await moderator.GetAsync(ListPath + "?publicationId=" + Guid.NewGuid(), ct)) { await AuthorizationMatrix.AssertAsync(absent, HttpStatusCode.NotFound); }
        var empty = await JsonAsync(await moderator.GetAsync(ListPath, ct), HttpStatusCode.OK, ct); Assert.Empty(empty.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task OpenApiDescribesModerationParametersSecurityResponsesAndPrivateShape()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var document = await JsonAsync(await f.Anonymous.GetAsync("/openapi/v1.json", ct), HttpStatusCode.OK, ct);
        await File.WriteAllTextAsync(ArtifactPath("us5-openapi.json"), document.GetRawText(), ct);
        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("ModerationComment");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean()); Assert.Equal(10, schema.GetProperty("required").GetArrayLength());
        Assert.Equal(3, schema.GetProperty("oneOf").GetArrayLength());
        foreach (var (path, method) in new[] { (ListPath, "get"), ("/api/v1/comments/{commentId}/hide", "post"), ("/api/v1/comments/{commentId}/restore", "post") })
        {
            var operation = document.GetProperty("paths").GetProperty(path).GetProperty(method);
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _)); Assert.False(operation.TryGetProperty("requestBody", out _));
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            if (method == "get") { Assert.Equal(new[] { "page", "pageSize", "publicationId", "state" }, parameters.Select(p => p.GetProperty("name").GetString())); }
            else { Assert.True(Assert.Single(parameters, p => p.GetProperty("name").GetString() == "X-Resource-Version").GetProperty("required").GetBoolean()); }
            var responses = operation.GetProperty("responses");
            foreach (var status in new[] { "200", "400", "401", "403", "404", "500", "503" })
            {
                Assert.True(responses.TryGetProperty(status, out var response)); Assert.True(response.GetProperty("headers").TryGetProperty("Cache-Control", out _));
                if (status != "200") { Assert.True(response.GetProperty("content").TryGetProperty("application/problem+json", out _)); }
            }
            if (method == "post") { foreach (var status in new[] { "409", "413", "415" }) { Assert.True(responses.TryGetProperty(status, out _)); } }
        }
    }
}
