using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BlogEngine.IntegrationTests.Setup;

[Trait("Category", "Setup")]
public sealed class DatabaseFixtureTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task MigratedDatabasesAreIsolatedAndConnectionsAreIndependent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = await postgres.CreateDatabaseAsync(CreateContext, cancellationToken);
        await using var second = await postgres.CreateDatabaseAsync(CreateContext, cancellationToken);
        await using var writer = CreateContext(first.ConnectionString);
        await using var reader = CreateContext(first.ConnectionString);
        await using var isolated = CreateContext(second.ConnectionString);

        Assert.NotSame(writer.Database.GetDbConnection(), reader.Database.GetDbConnection());
        await writer.Database.OpenConnectionAsync(cancellationToken);
        await reader.Database.OpenConnectionAsync(cancellationToken);
        var writerPid = await writer.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(cancellationToken);
        var readerPid = await reader.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(cancellationToken);
        Assert.NotEqual(writerPid, readerPid);
        await writer.Database.CloseConnectionAsync();
        await writer.Database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(writerPid, await writer.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(cancellationToken));
        Assert.Equal(["20261024000000_FixtureProbe"], await reader.Database.GetAppliedMigrationsAsync(cancellationToken));
        await writer.Database.ExecuteSqlRawAsync("INSERT INTO fixture_probe (id) VALUES (1)", cancellationToken);
        Assert.Equal(1, await reader.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM fixture_probe").SingleAsync(cancellationToken));
        Assert.Equal(0, await isolated.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM fixture_probe").SingleAsync(cancellationToken));
    }

    [Fact]
    public async Task DatabaseUsesRequiredPostgresEncodingLocaleAndTimeZone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(CreateContext, cancellationToken);
        await using var context = CreateContext(database.ConnectionString);
        var settings = await context.Database.SqlQueryRaw<DatabaseSettings>(
            "SELECT current_setting('server_version') AS \"Version\", current_setting('server_encoding') AS \"Encoding\", current_setting('TimeZone') AS \"TimeZone\", datcollate AS \"Collation\" FROM pg_database WHERE datname = current_database()")
            .SingleAsync(cancellationToken);
        Assert.StartsWith("18.6", settings.Version, StringComparison.Ordinal);
        Assert.Equal("UTF8", settings.Encoding);
        Assert.Equal("UTC", settings.TimeZone);
        Assert.Equal("C", settings.Collation);
        Assert.Empty(await context.Database.SqlQueryRaw<string>(
            "SELECT extname AS \"Value\" FROM pg_extension WHERE extname <> 'plpgsql'").ToListAsync(cancellationToken));
    }

    private static ProbeDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ProbeDbContext>().UseNpgsql(connectionString).Options);

    private sealed class DatabaseSettings
    {
        public required string Version { get; init; }
        public required string Encoding { get; init; }
        public required string TimeZone { get; init; }
        public required string Collation { get; init; }
    }
}

// This migration tests the harness only. The production model begins in T016.
public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options);

[DbContext(typeof(ProbeDbContext))]
[Migration("20261024000000_FixtureProbe")]
public sealed class FixtureProbe : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable("fixture_probe", columns: table => new { id = table.Column<int>(nullable: false) },
            constraints: table => table.PrimaryKey("pk_fixture_probe", row => row.id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("fixture_probe");
}
