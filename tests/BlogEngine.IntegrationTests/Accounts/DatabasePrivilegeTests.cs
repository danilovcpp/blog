using System.Net;
using System.Net.Http.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlogEngine.IntegrationTests.Accounts;

[Trait("Category", "Authorization")]
public sealed class DatabasePrivilegeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ActualPrincipalsEnforceImmutableAuditAndSeparateOperatorFromContentAndDdl()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await OperatorFixture.StartAsync(postgres, ct);
        (await f.RunAsync(f.Change(), ct)).Success();
        foreach (var (connectionString, statements) in new[]
        {
            (f.RuntimeConnectionString, new[]
            {
                "SELECT * FROM accounts.role_change_audit", "INSERT INTO accounts.role_change_audit SELECT * FROM accounts.role_change_audit",
                "UPDATE accounts.role_change_audit SET reason='altered'", "DELETE FROM accounts.role_change_audit", "TRUNCATE accounts.role_change_audit",
                "INSERT INTO accounts.role_assignments SELECT id,3,now() FROM accounts.local_users",
                "UPDATE accounts.role_assignments SET role=3", "DELETE FROM accounts.role_assignments", "CREATE TABLE accounts.forbidden(id int)",
            }),
            (f.ConnectionString, new[]
            {
                "UPDATE accounts.role_change_audit SET reason='altered'", "DELETE FROM accounts.role_change_audit", "TRUNCATE accounts.role_change_audit",
                "UPDATE accounts.local_users SET issuer='changed'", "INSERT INTO accounts.local_users VALUES(gen_random_uuid(),'x',gen_random_uuid(),now())",
                "UPDATE accounts.role_assignments SET role=3", "CREATE TABLE accounts.forbidden(id int)", "CREATE SCHEMA forbidden", "CREATE TEMP TABLE forbidden(id int)",
                "SELECT * FROM publishing.publications", "UPDATE publishing.publications SET title='changed'", "DELETE FROM publishing.categories", "SELECT * FROM engagement.likes",
            }),
        })
        {
            await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
            foreach (var sql in statements)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState);
            }
        }
        await using var migration = new NpgsqlConnection(f.MigrationConnectionString); await migration.OpenAsync(ct);
        await using (var useOwner = new NpgsqlCommand($"SET ROLE \"{f.Roles["owner_role"]}\"", migration)) { await useOwner.ExecuteNonQueryAsync(ct); }
        await using (var ddl = new NpgsqlCommand("CREATE TABLE accounts.migration_probe(id int); DROP TABLE accounts.migration_probe", migration)) { await ddl.ExecuteNonQueryAsync(ct); }
        await using var db = BootstrapTests.Context(f.ConnectionString);
        Assert.Equal(1, await db.RoleChangeAudits.CountAsync(ct));
    }

    [Fact]
    public async Task OperatorChangesTakeEffectOnNextHttpOperationWithUnchangedJwtAndRuntimeCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await OperatorFixture.StartAsync(postgres, ct);
        await using var identity = new JwksFixture(); await identity.InitializeAsync();
        // Seed the same trusted identity into this project's mapping, without granting any role.
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var local = new BlogEngine.Core.Accounts.Domain.LocalUser(Guid.NewGuid(), identity.Issuer, f.Subject, DateTimeOffset.UtcNow);
        db.LocalUsers.Add(local); await db.SaveChangesAsync(ct);
        await using var factory = new ApiFactory(f.RuntimeConnectionString, identity);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken(f.Subject, customize: claims => claims["roles"] = new[] { "Admin" }));
        using var reader = await client.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content(), ct);
        await AuthorizationMatrix.AssertAsync(reader, HttpStatusCode.Forbidden);
        string[] Change(string action, string role) => ["roles", action, "--user-id", local.Id.ToString(), "--role", role, "--reason", "role refresh"];
        // Wrong configured issuer cannot administer the mapping even when its local UUID is known.
        (await f.RunAsync(Change("grant", "Author"), ct)).Error(3, "user_not_found");
        foreach (var role in new[] { "Author", "Moderator", "Admin" }) { (await f.RunAsync(Change("grant", role), ct, issuer: identity.Issuer)).Success(); }
        using var author = await client.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content(), ct);
        Assert.Equal(HttpStatusCode.Created, author.StatusCode);
        foreach (var role in new[] { "Admin", "Author" }) { (await f.RunAsync(Change("revoke", role), ct, issuer: identity.Issuer)).Success(); }
        using var revoked = await client.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("revoked"), ct);
        await AuthorizationMatrix.AssertAsync(revoked, HttpStatusCode.Forbidden);
        await using var runtime = new NpgsqlConnection(f.RuntimeConnectionString); await runtime.OpenAsync(ct);
        await using var grant = new NpgsqlCommand("INSERT INTO accounts.role_assignments SELECT id,3,now() FROM accounts.local_users", runtime);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => grant.ExecuteNonQueryAsync(ct))).SqlState);
        Assert.Equal(1, identity.RequestCount);
    }
}
