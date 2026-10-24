using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Discussions;

[Trait("Category", "Discussions")]
[Trait("Category", "Concurrency")]
public sealed class CommentConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task TwoInstancesHaveOneEditWinnerAndAncestorCountsDoNotRotateAncestorVersions()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "concurrency", ct); var pubId = pub.GetProperty("id").GetGuid();
        var root = await CreateAsync(author, pubId, null, ct); var rootId = root.GetProperty("id").GetGuid(); var child = await CreateAsync(author, pubId, rootId, ct); var childId = child.GetProperty("id").GetGuid();
        await using var factory = new ApiFactory(f.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BlogEngine.Api.Configuration.ApiOptions>>().Value.ConnectionString, f.Identity);
        using var second = factory.CreateClient(); second.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var barrier = new ConcurrencyBarrier(2); var clients = new[] { author, second };
        var edits = await Task.WhenAll(clients.Select(async (client, i) => { await barrier.SignalAndWaitAsync(ct); return await SendAsync(client, HttpMethod.Put, $"/api/v1/comments/{childId}", child.GetProperty("version").GetGuid(), new { content = "edit" + i }, ct); }));
        Assert.Single(edits, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(edits, x => x.StatusCode == HttpStatusCode.Conflict); foreach (var response in edits) { response.Dispose(); }
        var current = await JsonAsync(await author.GetAsync($"/api/v1/comments/{childId}", ct), HttpStatusCode.OK, ct);
        var grandchild = await CreateAsync(second, pubId, childId, ct); var grandchildId = grandchild.GetProperty("id").GetGuid();
        using (var deletion = await SendAsync(author, HttpMethod.Delete, $"/api/v1/comments/{childId}", current.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); var rootEntity = await db.Comments.AsNoTracking().SingleAsync(x => x.Id == rootId, ct);
        Assert.Equal(root.GetProperty("version").GetGuid(), rootEntity.Version); Assert.Equal(rootEntity.CreatedAt, rootEntity.UpdatedAt); Assert.Equal(1, rootEntity.VisibleDescendantCount);
        Assert.Equal(1, (await db.Comments.AsNoTracking().SingleAsync(x => x.Id == childId, ct)).VisibleDescendantCount);
        using (var deletion = await SendAsync(second, HttpMethod.Delete, $"/api/v1/comments/{grandchildId}", grandchild.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode); }
        Assert.Equal(0, (await db.Comments.AsNoTracking().SingleAsync(x => x.Id == rootId, ct)).VisibleDescendantCount);
        Assert.Equal(0, (await db.Comments.AsNoTracking().SingleAsync(x => x.Id == childId, ct)).VisibleDescendantCount);
        await AssertCountsAsync(db, ct);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("edit", false)]
    [InlineData("delete", false)]
    [InlineData("create", true)]
    [InlineData("edit", true)]
    [InlineData("delete", true)]
    public async Task PublicationClosingWinnerMakesWaitingCommentOperationUnavailable(string operation, bool deletePublication)
    {
        var ct = TestContext.Current.CancellationToken; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30)); ct = timeout.Token;
        await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct); var pub = await PublishedAsync(author, "close", ct); var pubId = pub.GetProperty("id").GetGuid();
        var root = await CreateAsync(author, pubId, null, ct); var id = root.GetProperty("id").GetGuid();
        await using var winner = new NpgsqlConnection(f.Database.ConnectionString); await winner.OpenAsync(ct); await using var tx = await winner.BeginTransactionAsync(ct);
        await using (var close = new NpgsqlCommand(deletePublication ? "UPDATE publishing.publications SET deleted_at=now(),version=gen_random_uuid() WHERE id=@p" : "UPDATE publishing.publications SET state=0,version=gen_random_uuid() WHERE id=@p", winner, tx))
        { close.Parameters.AddWithValue("p", pubId); await close.ExecuteNonQueryAsync(ct); }
        var pending = operation switch
        {
            "create" => author.PostAsJsonAsync($"/api/v1/publications/{pubId}/comments", new { content = "blocked", parentId = id }, ct),
            "edit" => SendAsync(author, HttpMethod.Put, $"/api/v1/comments/{id}", root.GetProperty("version").GetGuid(), new { content = "blocked" }, ct),
            _ => SendAsync(author, HttpMethod.Delete, $"/api/v1/comments/{id}", root.GetProperty("version").GetGuid(), null, ct),
        };
        await WaitForBlockedAsync(f.Database.ConnectionString, pending, ct); await tx.CommitAsync(ct);
        using var result = await pending; Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); var unchanged = await db.Comments.SingleAsync(ct); Assert.Equal(root.GetProperty("version").GetGuid(), unchanged.Version); Assert.Equal("comment", unchanged.Text);
    }

    [Fact]
    public async Task ParentDeletionAndReplyHaveValidSerialOutcomeAcrossConnections()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct); var pub = await PublishedAsync(author, "parent-race", ct); var pubId = pub.GetProperty("id").GetGuid();
        var root = await CreateAsync(author, pubId, null, ct); var id = root.GetProperty("id").GetGuid(); var barrier = new ConcurrencyBarrier(2);
        async Task<HttpResponseMessage> ReplyAsync() { await barrier.SignalAndWaitAsync(ct); return await author.PostAsJsonAsync($"/api/v1/publications/{pubId}/comments", new { content = "reply", parentId = id }, ct); }
        var create = ReplyAsync();
        await barrier.SignalAndWaitAsync(ct); using var deleted = await SendAsync(author, HttpMethod.Delete, $"/api/v1/comments/{id}", root.GetProperty("version").GetGuid(), null, ct); Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var created = await create; Assert.Contains(created.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.NotFound, HttpStatusCode.Conflict });
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertCountsAsync(db, ct);
    }

    [Fact]
    public async Task FailureAfterNodeWriteRollsBackNodeAndAllAncestorDeltas()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct); var pub = await PublishedAsync(author, "rollback", ct); var pubId = pub.GetProperty("id").GetGuid();
        var root = await CreateAsync(author, pubId, null, ct); var id = root.GetProperty("id").GetGuid(); var child = await CreateAsync(author, pubId, id, ct); var childId = child.GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION discussions.reject_aggregate() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$; CREATE TRIGGER reject_aggregate BEFORE UPDATE OF visible_descendant_count ON discussions.comments FOR EACH ROW EXECUTE FUNCTION discussions.reject_aggregate()", ct);
        using (var failed = await author.PostAsJsonAsync($"/api/v1/publications/{pubId}/comments", new { content = "rollback", parentId = childId }, ct)) { Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); }
        using (var failed = await SendAsync(author, HttpMethod.Delete, $"/api/v1/comments/{childId}", child.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); }
        Assert.Equal(2, await db.Comments.CountAsync(ct)); var preserved = await db.Comments.AsNoTracking().SingleAsync(x => x.Id == childId, ct); Assert.Equal(child.GetProperty("version").GetGuid(), preserved.Version); Assert.Equal("comment", preserved.Text); await AssertCountsAsync(db, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommentWinnerCommitsBeforeWaitingPublicationClose(bool deletePublication)
    {
        var ct = TestContext.Current.CancellationToken; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30)); ct = timeout.Token;
        var gate = new AncestorGate(); await using var f = await StartAsync(postgres, ct, gate); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "comment-first", ct); var pubId = pub.GetProperty("id").GetGuid(); var root = await CreateAsync(author, pubId, null, ct);
        gate.Arm(); var comment = author.PostAsJsonAsync($"/api/v1/publications/{pubId}/comments", new { content = "winner", parentId = root.GetProperty("id").GetGuid() }, ct);
        await gate.Entered.Task.WaitAsync(ct);
        var close = SendAsync(author, deletePublication ? HttpMethod.Delete : HttpMethod.Post,
            $"/api/v1/publications/{pubId}" + (deletePublication ? "" : "/unpublish"), pub.GetProperty("version").GetGuid(), null, ct);
        await WaitForBlockedAsync(f.Database.ConnectionString, close, ct); gate.Release.TrySetResult();
        using var created = await comment; Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var closed = await close; Assert.Equal(deletePublication ? HttpStatusCode.NoContent : HttpStatusCode.OK, closed.StatusCode);
        using var unavailable = await f.Anonymous.GetAsync($"/api/v1/publications/{pubId}/comments", ct); Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(2, await db.Comments.CountAsync(ct)); await AssertCountsAsync(db, ct);
    }

    [Fact]
    public async Task CancellationBetweenNodeAndAncestorWritesRollsBackEntireScope()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "cancel", ct); var pubId = pub.GetProperty("id").GetGuid(); var root = await CreateAsync(author, pubId, null, ct);
        var gate = new AncestorGate(); gate.Arm();
        await using var db = new BlogEngine.Infrastructure.Persistence.BlogDbContext(new DbContextOptionsBuilder<BlogEngine.Infrastructure.Persistence.BlogDbContext>()
            .UseNpgsql(f.Database.ConnectionString).AddInterceptors(gate).Options);
        var service = new BlogEngine.Core.Discussions.Application.CommentService(new BlogEngine.Infrastructure.Persistence.Discussions.CommentWriteStore(db,
            new BlogEngine.Infrastructure.Persistence.Publishing.PublicationWriteScope(db)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = service.CreateAsync(new CurrentActor(pub.GetProperty("author").GetProperty("id").GetGuid(), [LocalRole.Reader, LocalRole.Author]), pubId, root.GetProperty("id").GetGuid(), "cancelled", cancellation.Token);
        await gate.Entered.Task.WaitAsync(ct); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await using var verify = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(1, await verify.Comments.CountAsync(ct)); await AssertCountsAsync(verify, ct);
    }

    internal static async Task WaitForBlockedAsync(string connectionString, Task contender, CancellationToken ct)
    {
        await using var observer = new NpgsqlConnection(connectionString); await observer.OpenAsync(ct);
        while (true)
        {
            await using var waiting = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND cardinality(pg_blocking_pids(pid))>0)", observer);
            if ((bool)(await waiting.ExecuteScalarAsync(ct))!) { return; }
            if (contender.IsCompleted) { await contender; Assert.Fail("Expected a contender waiting for the publication lock."); }
            await Task.Yield();
        }
    }
    internal static async Task AssertCountsAsync(BlogEngine.Infrastructure.Persistence.BlogDbContext db, CancellationToken ct)
    {
        var mismatches = await db.Database.SqlQueryRaw<long>("""
            WITH RECURSIVE descendants AS (
              SELECT parent_id AS ancestor,id,state FROM discussions.comments WHERE parent_id IS NOT NULL
              UNION ALL SELECT c.parent_id,d.id,d.state FROM descendants d JOIN discussions.comments c ON c.id=d.ancestor WHERE c.parent_id IS NOT NULL
            ), counts AS (SELECT ancestor,count(*) AS visible FROM descendants WHERE state=0 GROUP BY ancestor)
            SELECT count(*) AS "Value" FROM discussions.comments c LEFT JOIN counts d ON d.ancestor=c.id
            WHERE c.visible_descendant_count<>coalesce(d.visible,0)
            """).SingleAsync(ct); Assert.Equal(0, mismatches);
    }

    private sealed class AncestorGate : DbCommandInterceptor
    {
        private int armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("WITH RECURSIVE ancestors", StringComparison.Ordinal) && Interlocked.Exchange(ref armed, 0) == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
}
