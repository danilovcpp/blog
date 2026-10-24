using System.Net;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Discussions;

[Trait("Category", "Discussions")]
public sealed class CommentConstraintTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task DatabaseEnforcesCommentConstraintsAndPrivilegesAndMigrationUpgrade()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "constraints", ct); var pubId = pub.GetProperty("id").GetGuid(); var comment = await CreateAsync(author, pubId, null, ct);
        var foreign = await PublishedAsync(author, "foreign-parent", ct); var foreignRoot = await CreateAsync(author, foreign.GetProperty("id").GetGuid(), null, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await db.Database.OpenConnectionAsync(ct);
        var id = comment.GetProperty("id").GetGuid();
        foreach (var (set, code, constraint) in new[]
        {
            ("id=NULL", "23502", ""), ("publication_id=NULL", "23502", ""), ("author_id=NULL", "23502", ""), ("depth=NULL", "23502", ""), ("state=NULL", "23502", ""),
            ("created_at=NULL", "23502", ""), ("updated_at=NULL", "23502", ""), ("version=NULL", "23502", ""), ("visible_descendant_count=NULL", "23502", ""),
            ("depth=0", "23514", "ck_comments_depth"), ("depth=6", "23514", "ck_comments_depth"), ("state=3", "23514", "ck_comments_state"),
            ("visible_descendant_count=-1", "23514", "ck_comments_count"), ("depth=2", "23514", "ck_comments_root"),
            ("parent_id=id,depth=2", "23514", "ck_comments_self_parent"),
            ("text=NULL", "23514", "ck_comments_text"), ("state=2", "23514", "ck_comments_text"),
            ("text=' padded '", "23514", "ck_comments_text"), ("text=repeat('😀',5001)", "23514", "ck_comments_text"),
            ("author_id=gen_random_uuid()", "23503", "fk_comments_author"), ("publication_id=gen_random_uuid()", "23503", "fk_comments_publication"),
        })
        {
            await using var command = new NpgsqlCommand("UPDATE discussions.comments SET " + set + " WHERE id=@id", (NpgsqlConnection)db.Database.GetDbConnection()); command.Parameters.AddWithValue("id", id);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct)); Assert.Equal(code, error.SqlState); if (constraint.Length > 0) { Assert.Equal(constraint, error.ConstraintName); }
        }
        var foreignId = foreignRoot.GetProperty("id").GetGuid();
        var fk = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE discussions.comments SET parent_id={foreignId},depth=2 WHERE id={id}", ct)); Assert.Equal("fk_comments_parent", fk.ConstraintName);
        foreach (var space in TextRules.WhiteSpace)
        {
            var padded = space + "😀" + space;
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE discussions.comments SET text={padded} WHERE id={id}", ct));
            Assert.Equal("ck_comments_text", error.ConstraintName);
            var normalized = TextRules.RequireTrimmed(padded, 5000, "content");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE discussions.comments SET text={normalized} WHERE id={id}", ct);
        }
        var exactLimit = string.Concat(Enumerable.Repeat("😀", 5000));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE discussions.comments SET text={exactLimit} WHERE id={id}", ct);
        foreach (var role in new[] { f.Roles["runtime_role"], f.Roles["operator_role"] })
        {
            await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct);
            await using (var setRole = new NpgsqlCommand($"SET ROLE \"{role}\"", connection)) { await setRole.ExecuteNonQueryAsync(ct); }
            var statements = role == f.Roles["runtime_role"] ? new[] { "DELETE FROM discussions.comments", "CREATE TABLE discussions.forbidden(id int)" } : new[] { "SELECT * FROM discussions.comments", "UPDATE discussions.comments SET text='stolen'" };
            foreach (var sql in statements) { await using var command = new NpgsqlCommand(sql, connection); Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState); }
        }
        Assert.False(db.Database.HasPendingModelChanges());
        var indexes = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname='discussions'").ToArrayAsync(ct);
        foreach (var index in new[] { "ix_comments_public_parent", "ix_comments_parent", "ix_comments_moderation", "ix_comments_publication_state" }) { Assert.Contains(indexes, x => x.Contains(index, StringComparison.Ordinal)); }
        await File.WriteAllTextAsync(ArtifactPath("us4-indexes.txt"), string.Join('\n', indexes), ct);
        await using var upgrade = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct); await using var context = BootstrapTests.Context(upgrade.ConnectionString);
        var migrator = context.GetService<IMigrator>(); var previous = context.Database.GetMigrations().Single(x => x.EndsWith("AddRoleChangeAudit", StringComparison.Ordinal));
        await migrator.MigrateAsync(previous, ct); await context.Database.ExecuteSqlRawAsync("INSERT INTO accounts.local_users VALUES ('11111111-1111-1111-1111-111111111111','urn:upgrade','22222222-2222-2222-2222-222222222222',now())", ct);
        await migrator.MigrateAsync(cancellationToken: ct); Assert.Equal(1, await context.LocalUsers.CountAsync(ct)); Assert.Equal(0, await context.Comments.CountAsync(ct));
        var script = migrator.GenerateScript(previous); Assert.Contains("CREATE TABLE discussions.comments", script, StringComparison.Ordinal);
        await File.WriteAllTextAsync(ArtifactPath("us4-migration.sql"), script, ct);
    }
}
