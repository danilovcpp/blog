using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Engagement.Domain;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Publishing;
using BlogEngine.Infrastructure.ReadModels;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Publishing;

[Trait("Category", "Publishing")]
[Trait("Category", "Concurrency")]
public sealed class PublishingConstraintTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ActualDatabaseEnforcesNamedConstraintsAndExactUnicodeTrim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct);
        var created = await JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", Content(), ct), HttpStatusCode.Created, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        await db.Database.OpenConnectionAsync(ct);
        foreach (var (set, constraint, state) in new[]
        {
            ("title = NULL", "publications_title_not_null", "23502"), ("title = ''", "ck_publications_title", "23514"),
            ("title = ' padded '", "ck_publications_title", "23514"), ("title = repeat('😀', 201)", "ck_publications_title", "23514"),
            ("slug = 'Bad'", "ck_publications_slug", "23514"), ("slug = E'slug\\n'", "ck_publications_slug", "23514"),
            ("markdown = repeat('é', 524289)", "ck_publications_markdown", "23514"),
            ("description = repeat('x', 501)", "ck_publications_description", "23514"),
            ("cover_url = 'http://example.test'", "ck_publications_cover_url", "23514"),
            ("state = 2", "ck_publications_state", "23514"), ("like_count = -1", "ck_publications_counters", "23514"),
            ("view_count = -1", "ck_publications_counters", "23514"), ("author_id = gen_random_uuid()", "fk_publications_local_users", "23503"),
            ("state = 1", "ck_publications_published", "23514"),
        })
        {
            // SQL fragments are the fixed negative cases above; no external input enters SQL syntax.
            await using var violation = new NpgsqlCommand("UPDATE publishing.publications SET " + set, (NpgsqlConnection)db.Database.GetDbConnection());
            var error = await Assert.ThrowsAsync<PostgresException>(() => violation.ExecuteNonQueryAsync(ct));
            Assert.Equal(state, error.SqlState);
            // PostgreSQL's NOT NULL diagnostics name the column; pg_constraint supplies its stable name.
            if (state != "23502") { Assert.Equal(constraint, error.ConstraintName); }
        }
        foreach (var space in TextRules.WhiteSpace)
        {
            var value = space + "😀" + space;
            var trimmed = await db.Database.SqlQueryRaw<string>("SELECT btrim(@value, @spaces) AS \"Value\"",
                new NpgsqlParameter("value", value), new NpgsqlParameter("spaces", TextRules.WhiteSpace)).SingleAsync(ct);
            Assert.Equal(TextRules.RequireTrimmed(value, 200, "title"), trimmed);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET title = {trimmed}", ct);
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET title = {value}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET state = 1, first_published_at = now(), markdown = {space.ToString()}", ct));
        }
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO publishing.publication_categories VALUES (gen_random_uuid(),gen_random_uuid())", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO engagement.likes VALUES (gen_random_uuid(),gen_random_uuid(),now())", ct));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(created.GetProperty("version").GetGuid(), (await db.Publications.SingleAsync(ct)).Version);
    }

    [Fact]
    public async Task ContentUpdatesPreserveCountersAndLikesPersonalizeWithoutWritingOnGet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct);
        var reader = await f.ActorAsync(LocalRole.Reader, ct);
        var created = await JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", Content(), ct), HttpStatusCode.Created, ct);
        var id = created.GetProperty("id").GetGuid(); var owner = created.GetProperty("author").GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        db.Likes.Add(new Like(id, owner, DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET like_count = 1, view_count = 97 WHERE id = {id}", ct);
        var edited = await JsonAsync(await SendAsync(author, HttpMethod.Put, "/api/v1/publications/" + id, created.GetProperty("version").GetGuid(), Content(markdown: "updated"), ct), HttpStatusCode.OK, ct);
        Assert.Equal(1, edited.GetProperty("likeCount").GetInt64()); Assert.Equal(97, edited.GetProperty("viewCount").GetInt64());
        var published = await JsonAsync(await SendAsync(author, HttpMethod.Post, "/api/v1/publications/" + id + "/publish", edited.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        foreach (var (client, liked) in new[] { (author, true), (reader, false), (f.Anonymous, false) })
        {
            var detail = await JsonAsync(await client.GetAsync("/api/v1/publications/" + id, ct), HttpStatusCode.OK, ct);
            Assert.Equal(liked, detail.GetProperty("isLikedByCurrentUser").GetBoolean());
            Assert.Equal(1, detail.GetProperty("likeCount").GetInt64()); Assert.Equal(97, detail.GetProperty("viewCount").GetInt64());
        }
        db.ChangeTracker.Clear(); var entity = await db.Publications.SingleAsync(ct);
        Assert.Equal(published.GetProperty("version").GetGuid(), entity.Version); Assert.Equal(97, entity.ViewCount); Assert.Single(await db.Likes.ToListAsync(ct));
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct);
        await using var useOperator = new NpgsqlCommand($"SET ROLE \"{f.Roles["operator_role"]}\"", connection); await useOperator.ExecuteNonQueryAsync(ct);
        foreach (var sql in new[] { "SELECT * FROM publishing.publications", "UPDATE publishing.publications SET like_count=0", "SELECT * FROM engagement.likes" })
        { await using var command = new NpgsqlCommand(sql, connection); Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState); }
    }

    [Fact]
    public async Task MigrationUpgradePreservesAccountsAndBoundedQueriesHaveRecordedSqlAndPlans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        var capture = new SqlCapture();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(capture).Options);
        var migrator = db.GetService<IMigrator>();
        var initial = db.Database.GetMigrations().First();
        await migrator.MigrateAsync(initial, ct);
        var owner = new LocalUser(Guid.NewGuid(), "urn:publishing-fixture", Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.LocalUsers.Add(owner); await db.SaveChangesAsync(ct);
        await db.Database.MigrateAsync(ct);
        Assert.Equal(owner.Id, (await db.LocalUsers.AsNoTracking().SingleAsync(ct)).Id);
        Assert.False(db.Database.HasPendingModelChanges());
        var indexDefinitions = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname='publishing' ORDER BY indexname").ToListAsync(ct);
        Assert.Contains(indexDefinitions, x => x.Contains("uq_publications_slug", StringComparison.Ordinal) && !x.Contains("WHERE", StringComparison.Ordinal));
        foreach (var index in new[] { "ix_publications_recent", "ix_publications_popular", "ix_publications_workspace_author", "ix_publications_workspace_admin", "ix_categories_name_id", "ix_tags_name_id", "ix_publication_categories_reverse", "ix_publication_tags_reverse" })
        { Assert.Contains(indexDefinitions, x => x.Contains(index, StringComparison.Ordinal)); }
        var category = new BlogEngine.Core.Publishing.Domain.Category("Name", "name"); db.Categories.Add(category); await db.SaveChangesAsync(ct);
        capture.Commands.Clear();
        await new TaxonomyQueries(db).ListAsync(TaxonomyKind.Category, new PageRequest(int.MaxValue, 100), ct);
        Assert.Single(capture.Commands); var sql = capture.Commands[0]; Assert.Contains("OFFSET", sql, StringComparison.Ordinal); Assert.Contains("COLLATE", sql, StringComparison.Ordinal);
        await File.WriteAllTextAsync(ArtifactPath("queries/us1-taxonomy.sql"), sql, ct);
        var plan = await db.Database.SqlQueryRaw<JsonDocument>("EXPLAIN (FORMAT JSON) SELECT id,name,slug,version FROM publishing.categories ORDER BY name COLLATE \"C\",id LIMIT 101 OFFSET 214748364600").ToListAsync(ct);
        await File.WriteAllTextAsync(ArtifactPath("queries/us1-taxonomy-plan.json"), plan[0].RootElement.GetRawText(), ct);
        foreach (var document in plan) { document.Dispose(); }
        await File.WriteAllTextAsync(ArtifactPath("us1-indexes.txt"), string.Join('\n', indexDefinitions), ct);
        var publication = BlogEngine.Core.Publishing.Domain.Publication.Create(owner.Id, new("Title", "hello", "body", null, null, [], []), DateTimeOffset.UtcNow);
        db.Publications.Add(publication); await db.SaveChangesAsync(ct);
        capture.Commands.Clear();
        var workspace = await new WorkspaceQueries(db).ListAsync(new CurrentActor(owner.Id, [LocalRole.Reader, LocalRole.Author]), new PageRequest(), null, ct);
        Assert.Single(workspace.Items); Assert.Null(workspace.Items[0].Publication.Markdown); Assert.Equal(3, capture.Commands.Count);
        Assert.DoesNotContain(".markdown", capture.Commands[0], StringComparison.Ordinal);
        await File.WriteAllTextAsync(ArtifactPath("queries/us1-workspace.sql"), string.Join("\n\n", capture.Commands), ct);
        var workspacePlan = await db.Database.SqlQuery<JsonDocument>($"""
            EXPLAIN (FORMAT JSON) SELECT id,title,slug,description,cover_url,author_id,state,created_at,updated_at,first_published_at,version,like_count,view_count,
            EXISTS (SELECT 1 FROM engagement.likes l WHERE l.publication_id = p.id AND l.user_id = {owner.Id})
            FROM publishing.publications p WHERE deleted_at IS NULL AND author_id = {owner.Id}
            ORDER BY updated_at DESC,id DESC LIMIT 21 OFFSET 0
            """).ToListAsync(ct);
        await File.WriteAllTextAsync(ArtifactPath("queries/us1-workspace-plan.json"), workspacePlan[0].RootElement.GetRawText(), ct);
        foreach (var document in workspacePlan) { document.Dispose(); }
        publication.Publish(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); var version = publication.Version;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PublicationWriteScope(db).ExecuteAsync<bool>(publication.Id, async (session, token) =>
        {
            await session.AdjustCountersAsync(new CounterDelta(1, 1), token);
            throw new InvalidOperationException("Controlled rollback.");
        }, ct));
        await db.Entry(publication).ReloadAsync(ct);
        Assert.Equal(0, publication.LikeCount); Assert.Equal(0, publication.ViewCount); Assert.Equal(version, publication.Version);
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
