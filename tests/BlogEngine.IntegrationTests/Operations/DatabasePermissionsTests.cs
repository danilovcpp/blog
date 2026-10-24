using System.Net;
using System.Security.Cryptography;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlogEngine.IntegrationTests.Operations;

[Trait("Category", "Operations")]
public sealed class DatabasePermissionsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ActualPermissionScriptAllowsBootstrapButDeniesDdlMappingChangesAndRoleWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        var roles = new[] { "owner_role", "migration_role", "runtime_role", "operator_role" }
            .ToDictionary(key => key, _ => "fixture_" + Guid.NewGuid().ToString("N"));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var admin = new NpgsqlConnection(database.ConnectionString);
        await admin.OpenAsync(ct);
        foreach (var (kind, role) in roles)
        {
            // Identifiers/password are generated hex, never taken from a request or written to output.
            var login = kind == "runtime_role" ? $"LOGIN PASSWORD '{password}'" : "NOLOGIN";
            await using var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" {login} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS", admin);
            await create.ExecuteNonQueryAsync(ct);
        }
        await postgres.ApplyPermissionsAsync(database, roles, ct);
        var runtimeConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = roles["runtime_role"],
            Password = password,
        }.ConnectionString;
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(runtimeConnection, identity);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken());
        using var response = await client.GetAsync("/__foundation/actor", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var runtime = new NpgsqlConnection(runtimeConnection);
        await runtime.OpenAsync(ct);
        foreach (var sql in new[]
        {
            "UPDATE accounts.local_users SET issuer='changed'", "DELETE FROM accounts.local_users",
            "INSERT INTO accounts.role_assignments SELECT id,1,now() FROM accounts.local_users",
            "UPDATE accounts.role_assignments SET role=3", "DELETE FROM accounts.role_assignments",
            "CREATE TABLE accounts.forbidden(id int)", "CREATE TABLE public.forbidden(id int)",
            "CREATE SCHEMA forbidden", "CREATE TEMP TABLE forbidden(id int)",
        })
        {
            await using var command = new NpgsqlCommand(sql, runtime);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState);
        }
        await using var setOperator = new NpgsqlCommand($"SET ROLE \"{roles["operator_role"]}\"", admin);
        await setOperator.ExecuteNonQueryAsync(ct);
        await using var select = new NpgsqlCommand("SELECT count(*) FROM accounts.local_users", admin);
        Assert.Equal(1L, await select.ExecuteScalarAsync(ct));
        await using var forbidden = new NpgsqlCommand("INSERT INTO accounts.role_assignments SELECT id,3,now() FROM accounts.local_users", admin);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync(ct))).SqlState);
    }
}
