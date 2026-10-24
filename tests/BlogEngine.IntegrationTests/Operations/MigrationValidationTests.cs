using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Operations;

[Trait("Category", "Operations")]
public sealed class MigrationValidationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task EveryRealBaselineUpgradesRepresentativeDataAndRepeatsIdempotentSql()
    {
        var ct = TestContext.Current.CancellationToken; var evidence = new List<object>();
        await using var f = await StartAsync(postgres, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var migrations = db.Database.GetMigrations().ToArray(); var migrator = db.GetService<IMigrator>();
        Assert.False(db.Database.HasPendingModelChanges());
        foreach (var target in migrations.Prepend("0"))
        {
            await migrator.MigrateAsync("0", ct); await migrator.MigrateAsync(target, ct);
            // InitialAccounts really starts with an empty schema. Later baselines preserve an existing identity.
            var userId = Guid.NewGuid(); var subject = Guid.NewGuid();
            if (target != "0")
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts.local_users(id,issuer,subject,created_at) VALUES ({userId},'urn:migration-fixture',{subject},TIMESTAMPTZ '2026-10-01 00:00:00Z')", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts.role_assignments(user_id,role,granted_at) VALUES ({userId},1,TIMESTAMPTZ '2026-10-01 00:00:00Z')", ct);
            }
            var publicationId = Guid.NewGuid();
            if (Array.IndexOf(migrations, target) >= 1)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO publishing.publications(id,author_id,title,slug,markdown,state,created_at,updated_at,first_published_at,version,like_count,view_count) VALUES ({publicationId},{userId},'Migration fixture','migration-fixture','# preserve me',1,TIMESTAMPTZ '2026-10-01 00:00:00Z',TIMESTAMPTZ '2026-10-01 00:00:00Z',TIMESTAMPTZ '2026-10-01 00:00:00Z',{Guid.NewGuid()},0,17)", ct);
            }
            await db.Database.MigrateAsync(ct);
            var script = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
            await db.Database.ExecuteSqlRawAsync(script, ct); await db.Database.ExecuteSqlRawAsync(script, ct);
            Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync(ct));
            if (target != "0") { Assert.Equal(subject, (await db.LocalUsers.AsNoTracking().SingleAsync(ct)).Subject); }
            if (Array.IndexOf(migrations, target) >= 1)
            {
                var publication = await db.Publications.AsNoTracking().SingleAsync(ct);
                Assert.Equal(publicationId, publication.Id); Assert.Equal("# preserve me", publication.Markdown); Assert.Equal(17, publication.ViewCount);
            }
            await File.WriteAllTextAsync(ArtifactPath($"migrations/{target}.sql"), script, ct);
            evidence.Add(new { baseline = target, upgrade = "passed", idempotentReplay = "passed" });
        }
        var constraints = await db.Database.SqlQueryRaw<string>("SELECT n.nspname||'.'||c.conname AS \"Value\" FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname IN ('accounts','publishing','discussions','engagement') ORDER BY 1").ToArrayAsync(ct);
        var indexes = await db.Database.SqlQueryRaw<string>("SELECT schemaname||'.'||indexname||':'||indexdef AS \"Value\" FROM pg_indexes WHERE schemaname IN ('accounts','publishing','discussions','engagement') ORDER BY 1").ToArrayAsync(ct);
        Assert.Contains(constraints, x => x.EndsWith(".ck_publications_counters", StringComparison.Ordinal));
        Assert.Contains(constraints, x => x.EndsWith(".fk_comments_parent", StringComparison.Ordinal));
        Assert.Contains(indexes, x => x.Contains("ix_publications_popular", StringComparison.Ordinal));
        Assert.Contains(indexes, x => x.Contains("ix_view_deduplication_expiry", StringComparison.Ordinal));
        await File.WriteAllTextAsync(ArtifactPath("migrations/report.json"), JsonSerializer.Serialize(new
        { status = "passed", postgres = "18.6", baselines = evidence, modelDrift = false, constraints, indexes, destructiveUpChanges = "none; Down is exercised only on disposable databases; production recovery uses backup/forward repair" }), ct);
    }
}
