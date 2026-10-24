using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Http;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.IntegrationTests.Accounts;

[Trait("Category", "Authorization")]
[Trait("Category", "Authentication")]
[Trait("Category", "ConsumerContract")]
public sealed class MeContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("anonymous")]
    [InlineData("Reader")]
    [InlineData("Author")]
    [InlineData("Moderator")]
    [InlineData("Author,Moderator")]
    [InlineData("Admin")]
    public async Task HttpMatrixChecksActionOwnershipAndVisibilityBeforeVersion(string assignment)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await PublishingFixture.StartAsync(postgres, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var user = new LocalUser(Guid.NewGuid(), f.Identity.Issuer, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var other = new LocalUser(Guid.NewGuid(), f.Identity.Issuer, Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.LocalUsers.AddRange(user, other);
        var roles = assignment == "anonymous" ? [] : assignment.Split(',').Select(Enum.Parse<LocalRole>).ToArray();
        foreach (var role in roles.Where(r => r != LocalRole.Reader)) { db.RoleAssignments.Add(new(user.Id, role, DateTimeOffset.UtcNow)); }
        await db.SaveChangesAsync(ct);
        using var client = f.Factory.CreateClient();
        if (assignment != "anonymous") { client.DefaultRequestHeaders.Authorization = new("Bearer", f.Identity.CreateToken(user.Subject, customize: c => c["role"] = "Admin")); }
        foreach (var own in new[] { false, true })
        {
            foreach (var state in new[] { "draft", "published", "deleted" })
            {
                var content = new PublicationContent("matrix", "matrix-" + Guid.NewGuid().ToString("N"), "body", null, null, [], []);
                var publication = Publication.Create(own ? user.Id : other.Id, content, DateTimeOffset.UtcNow);
                if (state == "published") { publication.Publish(DateTimeOffset.UtcNow); }
                if (state == "deleted") { publication.Delete(DateTimeOffset.UtcNow); }
                db.Publications.Add(publication); await db.SaveChangesAsync(ct);
                var admin = roles.Contains(LocalRole.Admin);
                var status = assignment == "anonymous" ? HttpStatusCode.Unauthorized
                    : state == "deleted" || (state == "draft" && !own && !admin) ? HttpStatusCode.NotFound
                    : !admin && (!roles.Contains(LocalRole.Author) || !own) ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict;
                using var response = await PublishingFixture.SendAsync(client, HttpMethod.Put, "/api/v1/publications/" + publication.Id, Guid.NewGuid(),
                    PublishingFixture.Content(publication.Slug), ct);
                await AuthorizationMatrix.AssertAsync(response, status);
                if (status == HttpStatusCode.Conflict)
                {
                    using var injected = await PublishingFixture.SendAsync(client, HttpMethod.Put, "/api/v1/publications/" + publication.Id, publication.Version,
                        new { title = "x", slug = publication.Slug, markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), authorId = other.Id, roles = new[] { "Admin" } }, ct);
                    await InfrastructureContractTests.AssertProblem(injected, 400, "invalid_request");
                }
                db.ChangeTracker.Clear();
                var persisted = await db.Publications.AsNoTracking().SingleAsync(p => p.Id == publication.Id, ct);
                Assert.Equal(publication.AuthorId, persisted.AuthorId);
                Assert.Equal(publication.Version, persisted.Version);
            }
        }
    }

    [Fact]
    public async Task MeBootstrapsOnceReturnsOnlyIdAndOrderedRolesAndRefreshesTheSameJwt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(database.ConnectionString, identity);
        using var client = factory.CreateClient();
        using var anonymous = await client.GetAsync("/api/v1/me", ct);
        await AuthorizationMatrix.AssertAsync(anonymous, HttpStatusCode.Unauthorized);
        var subject = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken(subject, customize: c => c["roles"] = new[] { "Admin" }));
        var barrier = new ConcurrencyBarrier(100);
        var users = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await barrier.SignalAndWaitAsync(ct);
            using var response = await client.GetAsync("/api/v1/me", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(["id", "roles"], json.EnumerateObject().Select(p => p.Name));
            Assert.Equal("[\"Reader\"]", json.GetProperty("roles").GetRawText());
            return json.GetProperty("id").GetGuid();
        }));
        Assert.Single(users.Distinct());
        await using var context = BootstrapTests.Context(database.ConnectionString);
        Assert.Equal(1, await context.LocalUsers.CountAsync(ct));
        foreach (var role in new[] { LocalRole.Admin, LocalRole.Moderator, LocalRole.Author }) { context.RoleAssignments.Add(new(users[0], role, DateTimeOffset.UtcNow)); }
        await context.SaveChangesAsync(ct);
        var elevated = await client.GetFromJsonAsync<JsonElement>("/api/v1/me", ct);
        Assert.Equal("[\"Reader\",\"Author\",\"Moderator\",\"Admin\"]", elevated.GetProperty("roles").GetRawText());
        await context.RoleAssignments.ExecuteDeleteAsync(ct);
        Assert.Equal("[\"Reader\"]", (await client.GetFromJsonAsync<JsonElement>("/api/v1/me", ct)).GetProperty("roles").GetRawText());
        foreach (var path in new[] { "/api/v1/users", "/api/v1/roles", "/api/v1/me/roles", $"/api/v1/users/{users[0]}" })
        {
            using var hidden = await client.PostAsJsonAsync(path, new { role = "Admin" }, ct);
            await AuthorizationMatrix.AssertAsync(hidden, HttpStatusCode.NotFound);
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken(subject, issuer: identity.Issuer + "/"));
        using var wrongIssuer = await client.GetAsync("/api/v1/me", ct);
        await InfrastructureContractTests.AssertProblem(wrongIssuer, 401, "invalid_token");
        client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken(subject, audience: "other-project"));
        using var wrongProject = await client.GetAsync("/api/v1/me", ct);
        await InfrastructureContractTests.AssertProblem(wrongProject, 401, "invalid_token");
        Assert.Equal(1, await context.LocalUsers.CountAsync(ct));
    }

    [Fact]
    public async Task SameSubjectFromAnotherConfiguredInstallationHasAnotherLocalId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        await using var context = BootstrapTests.Context(database.ConnectionString);
        var resolver = new BlogEngine.Infrastructure.Persistence.Accounts.ActorResolver(context);
        var subject = Guid.NewGuid();
        var first = await resolver.ResolveAsync(new("urn:first", subject), ct);
        var second = await resolver.ResolveAsync(new("urn:second", subject), ct);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task MeReportsDatabaseAndKeyOutagesSafely()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken());
        using var unavailable = await client.GetAsync("/api/v1/me", ct);
        await InfrastructureContractTests.AssertProblem(unavailable, 503, "dependency_unavailable");
        identity.Time.Advance(TimeSpan.FromSeconds(301));
        identity.SetResponse("down", HttpStatusCode.ServiceUnavailable);
        using var keys = await client.GetAsync("/api/v1/me", ct);
        await InfrastructureContractTests.AssertProblem(keys, 503, "identity_keys_unavailable");
    }
}
