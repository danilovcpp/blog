using System.Net;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Engagement.ViewContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Engagement;

[Trait("Category", "Views")]
[Trait("Category", "Operations")]
public sealed class ViewMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task UpgradeDownReapplyIdempotentSqlPreserveContentAndCounters()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "view-migrations", ct); var id = pub.GetProperty("id").GetGuid();
        await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); var migrator = db.GetService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToArray(); await migrator.MigrateAsync(migrations[^2], ct);
        Assert.Equal(1, (await db.Publications.AsNoTracking().SingleAsync(ct)).ViewCount);
        await db.Database.MigrateAsync(ct); Assert.Equal(0, await db.ViewDeduplicationRecords.CountAsync(ct));
        var script = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await db.Database.ExecuteSqlRawAsync(script, ct); await db.Database.ExecuteSqlRawAsync(script, ct);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(pub.GetProperty("version").GetGuid(), (await db.Publications.AsNoTracking().SingleAsync(ct)).Version);
        await File.WriteAllTextAsync(ArtifactPath("us7-migrations.sql"), script, ct);
    }

    [Fact]
    public async Task RealConstraintsAndRestrictedPrincipalsProtectDeduplication()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var id = (await PublishedAsync(author, "view-constraints", ct)).GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        foreach (var (values, state) in new[]
        {
            ($"'{id}',0,decode(repeat('00',32),'hex'),now()", "23514"),
            ($"'{id}',2,decode('00','hex'),now()", "23514"),
            ($"'{Guid.NewGuid()}',2,decode(repeat('00',32),'hex'),now()", "23503"),
            ($"'{id}',2,NULL,now()", "23502"),
            ($"'{id}',2,decode(repeat('00',32),'hex'),NULL", "23502"),
        })
        {
            // Values here contain only fixture-generated UUIDs and static invalid literals.
            await using var command = new NpgsqlCommand("INSERT INTO engagement.view_deduplication_records VALUES (" + values + ")", new NpgsqlConnection(f.Database.ConnectionString));
            await command.Connection!.OpenAsync(ct); Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState); await command.Connection.DisposeAsync();
        }
        await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO engagement.view_deduplication_records SELECT * FROM engagement.view_deduplication_records", ct)); Assert.Equal("23505", duplicate.SqlState);
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct);
        await using (var role = new NpgsqlCommand($"SET ROLE \"{f.Roles["operator_role"]}\"", connection)) { await role.ExecuteNonQueryAsync(ct); }
        foreach (var sql in new[] { "SELECT * FROM engagement.view_deduplication_records", "DELETE FROM engagement.view_deduplication_records" })
        { await using var command = new NpgsqlCommand(sql, connection); Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState); }
    }
}
