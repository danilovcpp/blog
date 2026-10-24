using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Infrastructure.Visitors;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Engagement;

[Trait("Category", "Views")]
[Trait("Category", "ConsumerContract")]
public sealed class ViewContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(LocalRole.Reader)]
    [InlineData(LocalRole.Author)]
    [InlineData(LocalRole.Moderator)]
    [InlineData(LocalRole.Admin)]
    public async Task EveryLocalRoleCanRegisterOwnIdentity(LocalRole role)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var actor = await f.ActorAsync(role, ct);
        var id = (await PublishedAsync(author, "view-roles", ct)).GetProperty("id").GetGuid();
        var result = await JsonAsync(await ViewAsync(actor, id, null, ct), HttpStatusCode.OK, ct);
        Assert.True(result.GetProperty("counted").GetBoolean()); Assert.Equal(JsonValueKind.Null, result.GetProperty("anonymousVisitorId").ValueKind);
    }

    [Fact]
    public async Task StagedRotationAcrossIndependentProvidersPreservesOldTokenAndRemovesOnlyRetiredIdentity()
    {
        var ct = TestContext.Current.CancellationToken; await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var id = (await PublishedAsync(author, "view-rotation", ct)).GetProperty("id").GetGuid();
        var token = (await JsonAsync(await ViewAsync(f.Anonymous, id, null, ct), HttpStatusCode.OK, ct)).GetProperty("anonymousVisitorId").GetString();
        var ring = new Dictionary<string, string> { ["new"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ["fixture"] = f.Factory.VisitorKeys["fixture"] };
        var connection = f.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var rotated = new ApiFactory(connection, f.Identity) { VisitorKeys = ring, ViewClock = hosts.Clock }; using var client = rotated.CreateClient();
        Assert.NotSame(f.Factory.Services, rotated.Services);
        Assert.False((await JsonAsync(await ViewAsync(client, id, token, ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
        var issued = await JsonAsync(await ViewAsync(client, id, null, ct), HttpStatusCode.OK, ct); Assert.StartsWith("v1.new.", issued.GetProperty("anonymousVisitorId").GetString());
        ring.Remove("fixture");
        await using var retired = new ApiFactory(connection, f.Identity) { VisitorKeys = ring, ViewClock = hosts.Clock }; using var retiredClient = retired.CreateClient();
        var replaced = await JsonAsync(await ViewAsync(retiredClient, id, token, ct), HttpStatusCode.OK, ct);
        Assert.True(replaced.GetProperty("counted").GetBoolean()); Assert.Equal(3, replaced.GetProperty("viewCount").GetInt64());
        Assert.DoesNotContain(rotated.Logs.Entries.Concat(retired.Logs.Entries), entry => entry.Message.Contains(token!, StringComparison.Ordinal));
    }

    internal static async Task<HttpResponseMessage> ViewAsync(HttpClient client, Guid id, string? visitor, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/publications/{id}/views");
        if (visitor is not null) { request.Headers.TryAddWithoutValidation("X-Anonymous-Visitor", visitor); }
        return await client.SendAsync(request, ct);
    }

    [Fact]
    public async Task AnonymousIdentityBearerPriorityAndReadsRespectContract()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var pub = await PublishedAsync(author, "view-contract", ct); var id = pub.GetProperty("id").GetGuid();
        var first = await JsonAsync(await ViewAsync(f.Anonymous, id, null, ct), HttpStatusCode.OK, ct);
        var token = first.GetProperty("anonymousVisitorId").GetString()!; Assert.True(first.GetProperty("counted").GetBoolean()); Assert.Equal(1, first.GetProperty("viewCount").GetInt64());
        Assert.Equal(3, first.EnumerateObject().Count());
        using (var response = await ViewAsync(f.Anonymous, id, token, ct))
        {
            Assert.True(response.Headers.CacheControl!.NoStore); Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
            var repeat = await response.Content.ReadFromJsonAsync<JsonElement>(ct); Assert.False(repeat.GetProperty("counted").GetBoolean()); Assert.Equal(token, repeat.GetProperty("anonymousVisitorId").GetString());
        }
        var signed = await JsonAsync(await ViewAsync(author, id, token, ct), HttpStatusCode.OK, ct);
        Assert.True(signed.GetProperty("counted").GetBoolean()); Assert.Equal(JsonValueKind.Null, signed.GetProperty("anonymousVisitorId").ValueKind);
        Assert.False((await JsonAsync(await ViewAsync(author, id, "forged", ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
        foreach (var value in new[] { "forged", new string('x', 257), "" })
        { Assert.True((await JsonAsync(await ViewAsync(f.Anonymous, id, value, ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean()); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); var before = await db.Publications.AsNoTracking().SingleAsync(ct);
        foreach (var path in new[] { "/api/v1/publications", $"/api/v1/publications/{id}", $"/api/v1/publications/{id}/counters", $"/api/v1/publications/{id}/comments" })
        { using var response = await f.Anonymous.GetAsync(path, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
        Assert.Equal(5, (await db.Publications.AsNoTracking().SingleAsync(ct)).ViewCount); Assert.Equal(5, await db.ViewDeduplicationRecords.CountAsync(ct));
        Assert.Equal(pub.GetProperty("version").GetGuid(), before.Version);
        Assert.DoesNotContain(f.Factory.Logs.Entries, e => e.Message.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidBearerBodyAndClosedResourceDoNotIssueTokenOrWrite()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var pub = await PublishedAsync(author, "view-denial", ct); var id = pub.GetProperty("id").GetGuid();
        f.Anonymous.DefaultRequestHeaders.Authorization = new("Bearer", "bad");
        using (var denied = await ViewAsync(f.Anonymous, id, null, ct)) { await InfrastructureContractTests.AssertProblem(denied, 401, "invalid_token"); }
        f.Anonymous.DefaultRequestHeaders.Authorization = null;
        using (var body = await SendAsync(f.Anonymous, HttpMethod.Post, $"/api/v1/publications/{id}/views", null, new { visitorId = "chosen" }, ct))
        { await InfrastructureContractTests.AssertProblem(body, 400, "invalid_request"); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        foreach (var state in new[] { "draft", "deleted", "absent" })
        {
            if (state == "draft") { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET state=0 WHERE id={id}", ct); }
            if (state == "deleted") { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET state=1,deleted_at=now() WHERE id={id}", ct); }
            foreach (var client in new[] { f.Anonymous, author })
            {
                using var response = await ViewAsync(client, state == "absent" ? Guid.NewGuid() : id, null, ct);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
                Assert.DoesNotContain("anonymousVisitorId", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            }
        }
        Assert.Empty(await db.ViewDeduplicationRecords.ToListAsync(ct)); Assert.Equal(0, (await db.Publications.SingleAsync(ct)).ViewCount);
    }
}
