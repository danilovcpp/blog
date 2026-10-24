using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.IntegrationTests.Accounts;

[Trait("Category", "Authorization")]
[Trait("Category", "Concurrency")]
public sealed class BootstrapTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    internal static BlogDbContext Context(string connection) => new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(connection).Options);

    [Fact]
    public async Task ResolverUsesTheMigratedProductionModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct);
        await using var context = Context(database.ConnectionString);
        var actor = await new BlogEngine.Infrastructure.Persistence.Accounts.ActorResolver(context)
            .ResolveAsync(new BlogEngine.Core.Accounts.Contracts.VerifiedIdentity("urn:fixture", Guid.NewGuid()), ct);
        Assert.Equal([LocalRole.Reader], actor.Roles);
    }

    [Fact]
    public async Task ApiOptionsAndScopedDatabaseUseTheIsolatedFixtureConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct);
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(database.ConnectionString, identity);
        var options = factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value;
        Assert.True(options.ConnectionString == database.ConnectionString, "API configuration must use the isolated fixture connection.");
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        await context.Database.OpenConnectionAsync(ct);
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync(ct));
    }

    [Fact]
    public async Task HundredConcurrentFirstRequestsCreateOneReaderAndReReadRolesOnNextOperation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct);
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(database.ConnectionString, identity);
        using var client = factory.CreateClient();
        var subject = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            identity.CreateToken(subject, customize: c => { c["roles"] = new[] { "Admin" }; c["role"] = "Author"; }));
        var barrier = new ConcurrencyBarrier(100);
        var actors = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await barrier.SignalAndWaitAsync(ct);
            using var response = await client.GetAsync("/__foundation/actor", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, string.Join("\n", factory.Logs.Entries.Select(entry => entry.Message)));
            return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }));
        var id = actors[0].GetProperty("id").GetGuid();
        Assert.All(actors, actor =>
        {
            Assert.Equal(id, actor.GetProperty("id").GetGuid());
            Assert.Equal("[\"Reader\"]", actor.GetProperty("roles").GetRawText());
        });
        await using var context = Context(database.ConnectionString);
        Assert.Equal(1, await context.LocalUsers.CountAsync(ct));
        Assert.Equal(0, await context.RoleAssignments.CountAsync(ct));
        using var denied = await client.GetAsync("/__foundation/author", ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        context.RoleAssignments.Add(new RoleAssignment(id, LocalRole.Author, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync(ct);
        using var allowed = await client.GetAsync("/__foundation/author", ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await context.RoleAssignments.ExecuteDeleteAsync(ct);
        using var revoked = await client.GetAsync("/__foundation/author", ct);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Fact]
    public async Task InvalidJwtNeverBootstrapsAndAccountConstraintsAreEnforced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct);
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(database.ConnectionString, identity);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken(issuer: "untrusted"));
        using var denied = await client.GetAsync("/__foundation/actor", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await using var context = Context(database.ConnectionString);
        Assert.Equal(0, await context.LocalUsers.CountAsync(ct));
        var user = new LocalUser(Guid.NewGuid(), identity.Issuer, Guid.NewGuid(), DateTimeOffset.UtcNow);
        context.LocalUsers.Add(user);
        await context.SaveChangesAsync(ct);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        foreach (var (sql, state) in new[]
        {
            ("INSERT INTO accounts.local_users SELECT gen_random_uuid(), issuer, subject, created_at FROM accounts.local_users", "23505"),
            ("INSERT INTO accounts.role_assignments SELECT id, 0, now() FROM accounts.local_users", "23514"),
            ("INSERT INTO accounts.role_assignments VALUES (gen_random_uuid(), 1, now())", "23503"),
            ("INSERT INTO accounts.local_users VALUES (gen_random_uuid(), '', gen_random_uuid(), now())", "23514"),
            ("INSERT INTO accounts.local_users VALUES (gen_random_uuid(), NULL, gen_random_uuid(), now())", "23502"),
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
            Assert.Equal(state, error.SqlState);
        }
        context.RoleAssignments.Add(new RoleAssignment(user.Id, LocalRole.Moderator, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync(ct);
        await using var delete = new NpgsqlCommand("DELETE FROM accounts.local_users", connection);
        Assert.Equal("23001", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(ct))).SqlState);
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
