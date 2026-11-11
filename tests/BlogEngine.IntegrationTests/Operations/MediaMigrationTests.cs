using System.Diagnostics;
using System.Text.Json;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BlogEngine.IntegrationTests.Operations;

public sealed class MediaMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string MvpBaseline = "20261024081130_AddViewDeduplication";
    private static BlogDbContext Context(string connection) => new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(connection).Options);

    [Fact]
    public async Task AdditiveUpgradeKeepsMvpDataAndExternalCover()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct, targetMigration: MvpBaseline);
        await using var db = Context(database.ConnectionString);
        Assert.Contains(db.Database.GetMigrations(), migration => migration.EndsWith("_AddMedia", StringComparison.Ordinal));
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO accounts.local_users(id,issuer,subject,created_at) VALUES ('00000000-0000-0000-0000-000000000001','https://fixture','00000000-0000-0000-0000-000000000003',now());
            INSERT INTO publishing.publications(id,author_id,title,slug,markdown,cover_url,state,version,created_at,updated_at)
            VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Existing','existing','body','https://example.test/cover.png',0,gen_random_uuid(),now(),now());
            """, ct);
        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript(MvpBaseline,
            options: MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.NoTransactions);
        var timer = Stopwatch.StartNew();
        string[] locks;
        double elapsedMilliseconds;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s';", ct);
            await db.Database.ExecuteSqlRawAsync(script, ct);
            locks = await db.Database.SqlQueryRaw<string>("""
                SELECT DISTINCT n.nspname || '.' || c.relname || ':' || l.mode AS "Value"
                FROM pg_locks l JOIN pg_class c ON c.oid = l.relation
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE l.pid = pg_backend_pid() AND l.granted AND c.relkind = 'r'
                    AND n.nspname IN ('accounts', 'publishing', 'media')
                ORDER BY 1
                """).ToArrayAsync(ct);
            await transaction.CommitAsync(ct);
            elapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
        }
        Assert.Contains(locks, item => item.StartsWith("publishing.publications:", StringComparison.Ordinal));
        Assert.Contains(locks, item => item.StartsWith("accounts.local_users:", StringComparison.Ordinal));
        await db.Database.ExecuteSqlRawAsync(script, ct);
        await db.Database.ExecuteSqlRawAsync(script, ct);
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(ct));
        Assert.Equal("https://example.test/cover.png", await db.Publications.Select(p => p.CoverUrl).SingleAsync(ct));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT version AS \"Value\" FROM media.schema_version").SingleAsync(ct));
        Assert.DoesNotContain(db.Model.GetEntityTypes().SelectMany(t => t.GetKeys()), k => !k.IsPrimaryKey() && k.Properties.Any(p => p.Name == "State"));
        Assert.False(db.Database.HasPendingModelChanges());
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("migrations/media-upgrade.json"), JsonSerializer.Serialize(new
        {
            baseline = MvpBaseline,
            elapsedMilliseconds,
            locks,
            idempotentReplays = 2,
            preservedLegacyCover = true,
        }), ct);
    }

    [Fact]
    public async Task GrantsWorkBeforeAndAfterUpgradeWithoutDdlOrOperatorMediaAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct, targetMigration: MvpBaseline);
        await using var db = Context(database.ConnectionString);
        var roles = new[] { "owner_role", "migration_role", "runtime_role", "operator_role" }.ToDictionary(k => k, _ => "media_" + Guid.NewGuid().ToString("N"));
        await using var admin = new NpgsqlConnection(database.ConnectionString); await admin.OpenAsync(ct);
        foreach (var role in roles.Values)
        {
            await using var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS", admin);
            await create.ExecuteNonQueryAsync(ct);
        }
        await postgres.ApplyPermissionsAsync(database, roles, ct);
        await db.Database.MigrateAsync(ct); await postgres.ApplyPermissionsAsync(database, roles, ct);
        async Task SetRole(string key)
        {
            await using var command = new NpgsqlCommand($"SET ROLE \"{roles[key]}\"", admin); await command.ExecuteNonQueryAsync(ct);
        }
        await SetRole("runtime_role");
        await using (var marker = new NpgsqlCommand("SELECT version FROM media.schema_version", admin)) { Assert.Equal(1, await marker.ExecuteScalarAsync(ct)); }
        foreach (var sql in new[] { "CREATE TABLE media.forbidden(id int)", "CREATE SCHEMA forbidden", "DELETE FROM media.files", "UPDATE media.schema_version SET version=1" })
        {
            await using var command = new NpgsqlCommand(sql, admin);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState);
        }
        await using (var reset = new NpgsqlCommand("RESET ROLE", admin)) { await reset.ExecuteNonQueryAsync(ct); }
        await SetRole("operator_role");
        foreach (var table in new[] { "files", "uploads", "allowances", "avatar_states", "publication_usages", "objects", "representations", "cleanup_jobs", "schema_version" })
        {
            await using var read = new NpgsqlCommand($"SELECT * FROM media.{table} LIMIT 1", admin);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteNonQueryAsync(ct))).SqlState);
        }
    }
}
