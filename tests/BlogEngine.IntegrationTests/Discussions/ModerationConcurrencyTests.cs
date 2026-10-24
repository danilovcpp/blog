using System.Data.Common;
using System.Net;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Application;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Discussions;
using BlogEngine.Infrastructure.Persistence.Publishing;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.CommentConcurrencyTests;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Discussions.ModerationContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Discussions;

[Trait("Category", "Moderation")]
[Trait("Category", "Concurrency")]
public sealed class ModerationConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("hide", "hide", false)]
    [InlineData("hide", "restore", false)]
    [InlineData("hide", "edit", false)]
    [InlineData("hide", "delete", false)]
    [InlineData("restore", "delete", true)]
    [InlineData("restore", "edit", true)]
    public async Task TwoHostsHaveOneVersionWinnerAndPreserveAllFourAncestorCounts(string first, string second, bool hidden)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var admin = await f.ActorAsync(LocalRole.Admin, ct); var pub = await PublishedAsync(admin, "moderation-race", ct); var pubId = pub.GetProperty("id").GetGuid();
        var chain = new List<System.Text.Json.JsonElement>(); Guid? parent = null;
        for (var i = 0; i < 5; i++) { var node = await CreateAsync(admin, pubId, parent, ct); chain.Add(node); parent = node.GetProperty("id").GetGuid(); }
        var leaf = chain[^1]; var id = leaf.GetProperty("id").GetGuid();
        if (hidden) { leaf = await JsonAsync(await TransitionAsync(admin, leaf, "hide", ct), HttpStatusCode.OK, ct); }
        await using var factory = new ApiFactory(f.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BlogEngine.Api.Configuration.ApiOptions>>().Value.ConnectionString, f.Identity);
        using var other = factory.CreateClient(); other.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;
        var barrier = new ConcurrencyBarrier(2);
        async Task<HttpResponseMessage> Run(HttpClient client, string action)
        {
            await barrier.SignalAndWaitAsync(ct);
            return action switch
            {
                "edit" => await SendAsync(client, HttpMethod.Put, $"/api/v1/comments/{id}", leaf.GetProperty("version").GetGuid(), new { content = "edited" }, ct),
                "delete" => await SendAsync(client, HttpMethod.Delete, $"/api/v1/comments/{id}", leaf.GetProperty("version").GetGuid(), null, ct),
                _ => await TransitionAsync(client, leaf, action, ct),
            };
        }
        var results = await Task.WhenAll(Run(admin, first), Run(other, second));
        // A desired-state no-op may run first without consuming the version; every state-changing pair has one winner.
        if (first == "hide" && second == "restore")
        { Assert.Contains(results, r => r.StatusCode == HttpStatusCode.OK); Assert.All(results, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict })); }
        else { Assert.Single(results, r => r.IsSuccessStatusCode); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict); }
        foreach (var response in results) { response.Dispose(); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertCountsAsync(db, ct);
        foreach (var ancestor in chain.Take(4))
        {
            var entity = await db.Comments.AsNoTracking().SingleAsync(c => c.Id == ancestor.GetProperty("id").GetGuid(), ct);
            Assert.Equal(ancestor.GetProperty("version").GetGuid(), entity.Version); Assert.Equal(entity.CreatedAt, entity.UpdatedAt); Assert.Null(entity.EditedAt);
        }
        var current = await JsonAsync(await admin.GetAsync($"/api/v1/comments/{id}", ct), HttpStatusCode.OK, ct);
        if (current.GetProperty("state").GetString() != "Deleted")
        {
            var before = await db.Comments.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.Version, c.UpdatedAt, c.EditedAt, c.VisibleDescendantCount }).ToListAsync(ct);
            using var repeated = await TransitionAsync(admin, current, current.GetProperty("state").GetString() == "Hidden" ? "hide" : "restore", ct);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(before, await db.Comments.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.Version, c.UpdatedAt, c.EditedAt, c.VisibleDescendantCount }).ToListAsync(ct));
        }
    }

    [Theory]
    [InlineData("hide", false)]
    [InlineData("hide", true)]
    [InlineData("restore", false)]
    [InlineData("restore", true)]
    public async Task PublicationCloseIsRecheckedAfterLockBeforeVersion(string action, bool deletePublication)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var f = await StartAsync(postgres, ct); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(admin, "moderation-close", ct); var pubId = pub.GetProperty("id").GetGuid(); var node = await CreateAsync(admin, pubId, null, ct);
        if (action == "restore") { node = await JsonAsync(await TransitionAsync(admin, node, "hide", ct), HttpStatusCode.OK, ct); }
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand(deletePublication ? "UPDATE publishing.publications SET deleted_at=now() WHERE id=@id" : "UPDATE publishing.publications SET state=0 WHERE id=@id", connection, tx))
        { command.Parameters.AddWithValue("id", pubId); await command.ExecuteNonQueryAsync(ct); }
        var pending = SendAsync(admin, HttpMethod.Post, $"/api/v1/comments/{node.GetProperty("id").GetGuid()}/{action}", null, null, ct);
        await WaitForBlockedAsync(f.Database.ConnectionString, pending, ct); await tx.CommitAsync(ct);
        using var result = await pending; await AuthorizationMatrix.AssertAsync(result, HttpStatusCode.NotFound);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        Assert.Equal(node.GetProperty("version").GetGuid(), (await db.Comments.AsNoTracking().SingleAsync(ct)).Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterAncestorWritesRollsBackNodeAndEveryAncestor(bool restore)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(admin, "moderation-rollback", ct); var pubId = pub.GetProperty("id").GetGuid();
        System.Text.Json.JsonElement node = default; Guid? parent = null;
        for (var i = 0; i < 5; i++) { node = await CreateAsync(admin, pubId, parent, ct); parent = node.GetProperty("id").GetGuid(); }
        if (restore) { node = await JsonAsync(await TransitionAsync(admin, node, "hide", ct), HttpStatusCode.OK, ct); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var before = await db.Comments.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.State, c.Text, c.Version, c.UpdatedAt, c.EditedAt, c.VisibleDescendantCount }).ToListAsync(ct);
        // A deferred constraint trigger fails at commit, after the node and all ancestor updates have executed.
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION discussions.reject_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$; CREATE CONSTRAINT TRIGGER reject_commit AFTER UPDATE ON discussions.comments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION discussions.reject_commit()", ct);
        using var failed = await TransitionAsync(admin, node, restore ? "restore" : "hide", ct); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, await db.Comments.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.State, c.Text, c.Version, c.UpdatedAt, c.EditedAt, c.VisibleDescendantCount }).ToListAsync(ct));
        await AssertCountsAsync(db, ct);
    }

    [Fact]
    public async Task CancellationDuringAncestorUpdateRollsBackModeration()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(admin, "moderation-cancel", ct); var pubId = pub.GetProperty("id").GetGuid(); var root = await CreateAsync(admin, pubId, null, ct); var child = await CreateAsync(admin, pubId, root.GetProperty("id").GetGuid(), ct);
        var gate = new AncestorGate(); gate.Arm(); await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(gate).Options);
        var service = new ModerationService(new CommentWriteStore(db, new PublicationWriteScope(db)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = service.HideAsync(new CurrentActor(Guid.NewGuid(), [LocalRole.Admin]), child.GetProperty("id").GetGuid(), () => child.GetProperty("version").GetGuid(), cancellation.Token);
        await gate.Entered.Task.WaitAsync(ct); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await using var verify = BootstrapTests.Context(f.Database.ConnectionString); await AssertCountsAsync(verify, ct);
        Assert.Equal(child.GetProperty("version").GetGuid(), (await verify.Comments.SingleAsync(c => c.Id == child.GetProperty("id").GetGuid(), ct)).Version);
    }

    [Theory]
    [InlineData("hide", false)]
    [InlineData("hide", true)]
    [InlineData("restore", false)]
    [InlineData("restore", true)]
    public async Task ModerationWinnerCommitsBeforeWaitingPublicationClose(string action, bool deletePublication)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        var gate = new AncestorGate(); await using var f = await StartAsync(postgres, ct, gate); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(admin, "moderation-first", ct); var pubId = pub.GetProperty("id").GetGuid(); var root = await CreateAsync(admin, pubId, null, ct);
        var child = await CreateAsync(admin, pubId, root.GetProperty("id").GetGuid(), ct);
        if (action == "restore") { child = await JsonAsync(await TransitionAsync(admin, child, "hide", ct), HttpStatusCode.OK, ct); }
        gate.Arm(); var pending = TransitionAsync(admin, child, action, ct); await gate.Entered.Task.WaitAsync(ct);
        var close = SendAsync(admin, deletePublication ? HttpMethod.Delete : HttpMethod.Post,
            $"/api/v1/publications/{pubId}" + (deletePublication ? "" : "/unpublish"), pub.GetProperty("version").GetGuid(), null, ct);
        await WaitForBlockedAsync(f.Database.ConnectionString, close, ct); gate.Release.TrySetResult();
        using var changed = await pending; Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var closed = await close; Assert.Equal(deletePublication ? HttpStatusCode.NoContent : HttpStatusCode.OK, closed.StatusCode);
        using var unavailable = await admin.GetAsync(ListPath + "?publicationId=" + pubId, ct); await AuthorizationMatrix.AssertAsync(unavailable, HttpStatusCode.NotFound);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertCountsAsync(db, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentVisibilityAndNewReplyHaveValidSerialOutcome(bool initiallyHidden)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var pub = await PublishedAsync(admin, "moderation-parent", ct); var pubId = pub.GetProperty("id").GetGuid(); var root = await CreateAsync(admin, pubId, null, ct);
        if (initiallyHidden) { root = await JsonAsync(await TransitionAsync(admin, root, "hide", ct), HttpStatusCode.OK, ct); }
        var barrier = new ConcurrencyBarrier(2);
        async Task<HttpResponseMessage> ChangeAsync() { await barrier.SignalAndWaitAsync(ct); return await TransitionAsync(admin, root, initiallyHidden ? "restore" : "hide", ct); }
        var change = ChangeAsync(); await barrier.SignalAndWaitAsync(ct);
        using var reply = await SendAsync(admin, HttpMethod.Post, $"/api/v1/publications/{pubId}/comments", null, new { content = "reply", parentId = root.GetProperty("id").GetGuid() }, ct);
        using var changed = await change; Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Contains(reply.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.NotFound, HttpStatusCode.Conflict });
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertCountsAsync(db, ct);
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
