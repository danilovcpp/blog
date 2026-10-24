using System.Net;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Engagement.Application;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Engagement;
using BlogEngine.Infrastructure.Persistence.Publishing;
using BlogEngine.Infrastructure.Visitors;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.CommentConcurrencyTests;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Engagement.ViewContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Engagement;

[Trait("Category", "Views")]
[Trait("Category", "Concurrency")]
public sealed class ViewConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task HundredDifferentVisitorsHaveIndependentRecordsAcrossHosts()
    {
        var ct = TestContext.Current.CancellationToken; await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var id = (await PublishedAsync(author, "view-different", ct)).GetProperty("id").GetGuid();
        var tokens = new AnonymousVisitorTokenService("fixture", f.Factory.VisitorKeys);
        var barrier = new ConcurrencyBarrier(100);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            await barrier.SignalAndWaitAsync(ct);
            var result = await JsonAsync(await ViewAsync(i % 2 == 0 ? f.Anonymous : hosts.Replica, id, tokens.Issue(), ct), HttpStatusCode.OK, ct);
            Assert.True(result.GetProperty("counted").GetBoolean());
        }));
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(100, await db.ViewDeduplicationRecords.CountAsync(ct)); Assert.Equal(100, (await db.Publications.SingleAsync(ct)).ViewCount);
    }

    [Fact]
    public async Task DeferredCommitFailureRollsBackBothWrittenSourceAndCounter()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var id = (await PublishedAsync(author, "view-commit", ct)).GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION engagement.reject_view_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$; CREATE CONSTRAINT TRIGGER reject_view_commit AFTER INSERT ON engagement.view_deduplication_records DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION engagement.reject_view_commit()", ct);
        using var failed = await ViewAsync(author, id, null, ct); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await db.ViewDeduplicationRecords.CountAsync(ct)); Assert.Equal(0, (await db.Publications.SingleAsync(ct)).ViewCount);
    }

    [Fact]
    public async Task TimeIsSampledAfterWaitingForPublicationLock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var id = (await PublishedAsync(author, "view-clock", ct)).GetProperty("id").GetGuid();
        await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct); var initial = hosts.Clock.Now;
        hosts.Clock.Now = initial.AddHours(23);
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand("SELECT id FROM publishing.publications WHERE id=@id FOR UPDATE", connection, tx))
        { command.Parameters.AddWithValue("id", id); await command.ExecuteScalarAsync(ct); }
        hosts.Replica.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var pending = ViewAsync(hosts.Replica, id, null, ct); await WaitForBlockedAsync(f.Database.ConnectionString, pending, ct);
        hosts.Clock.Now = initial.AddHours(24); await tx.CommitAsync(ct);
        var result = await JsonAsync(await pending, HttpStatusCode.OK, ct); Assert.True(result.GetProperty("counted").GetBoolean()); Assert.Equal(2, result.GetProperty("viewCount").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedPublicationWinnerIsRecheckedWithoutIssuingToken(bool delete)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var id = (await PublishedAsync(author, "view-close", ct)).GetProperty("id").GetGuid();
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand(delete ? "UPDATE publishing.publications SET deleted_at=now() WHERE id=@id" : "UPDATE publishing.publications SET state=0 WHERE id=@id", connection, tx))
        { command.Parameters.AddWithValue("id", id); await command.ExecuteNonQueryAsync(ct); }
        var pending = ViewAsync(f.Anonymous, id, null, ct); await WaitForBlockedAsync(f.Database.ConnectionString, pending, ct); await tx.CommitAsync(ct);
        using var denied = await pending; Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); Assert.DoesNotContain("anonymousVisitorId", await denied.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(0, await db.ViewDeduplicationRecords.CountAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewWinnerCommitsBeforeWaitingClose(bool delete)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        var gate = new ViewCommandGate(sql => sql.StartsWith("UPDATE publishing.publications SET like_count", StringComparison.Ordinal));
        await using var f = await StartAsync(postgres, ct, gate); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "view-first", ct); var id = pub.GetProperty("id").GetGuid();
        gate.Arm(); var view = ViewAsync(f.Anonymous, id, null, ct); await gate.Entered.Task.WaitAsync(ct);
        var close = SendAsync(author, delete ? HttpMethod.Delete : HttpMethod.Post, $"/api/v1/publications/{id}" + (delete ? "" : "/unpublish"), pub.GetProperty("version").GetGuid(), null, ct);
        try { await WaitForBlockedAsync(f.Database.ConnectionString, close, ct); } finally { gate.Release.TrySetResult(); }
        Assert.True((await JsonAsync(await view, HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
        using var closed = await close; Assert.Equal(delete ? HttpStatusCode.NoContent : HttpStatusCode.OK, closed.StatusCode);
        using var denied = await ViewAsync(f.Anonymous, id, null, ct); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(1, (await db.Publications.SingleAsync(ct)).ViewCount);
    }

    [Fact]
    public async Task CancellationAfterDedupWriteRollsBackSourceAndAggregate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var id = (await PublishedAsync(author, "view-cancel", ct)).GetProperty("id").GetGuid();
        var gate = new ViewCommandGate(sql => sql.StartsWith("UPDATE publishing.publications SET like_count", StringComparison.Ordinal)); gate.Arm();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(gate).Options);
        var service = new ViewService(new ViewWriteStore(db, new PublicationWriteScope(db)));
        var visitor = new VisitorIdentityResolver(new AnonymousVisitorTokenService("fixture", f.Factory.VisitorKeys)).Resolve(null, null);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = service.RegisterAsync(id, visitor.Identity, cancel.Token); await gate.Entered.Task.WaitAsync(ct); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await using var verify = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(0, await verify.ViewDeduplicationRecords.CountAsync(ct)); Assert.Equal(0, (await verify.Publications.SingleAsync(ct)).ViewCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HundredRequestsAcrossHostsCountOnePerExactNonSlidingWindow(bool authenticated)
    {
        var ct = TestContext.Current.CancellationToken; await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var pub = await PublishedAsync(author, "view-hundred", ct); var id = pub.GetProperty("id").GetGuid();
        var token = new AnonymousVisitorTokenService("fixture", f.Factory.VisitorKeys).Issue();
        var first = authenticated ? author : f.Anonymous;
        hosts.Replica.DefaultRequestHeaders.Authorization = first.DefaultRequestHeaders.Authorization;
        var initial = hosts.Clock.Now;
        async Task RoundAsync(int expected, int counted)
        {
            var barrier = new ConcurrencyBarrier(100); var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
            {
                await barrier.SignalAndWaitAsync(ct);
                return await JsonAsync(await ViewAsync(i % 2 == 0 ? first : hosts.Replica, id, token, ct), HttpStatusCode.OK, ct);
            }));
            Assert.Equal(counted, results.Count(x => x.GetProperty("counted").GetBoolean()));
            Assert.All(results, x => Assert.Equal(expected, x.GetProperty("viewCount").GetInt64()));
        }
        await RoundAsync(1, 1);
        hosts.Clock.Now = initial.AddHours(23); await RoundAsync(1, 0);
        hosts.Clock.Now = initial.AddHours(24).AddTicks(-10); await RoundAsync(1, 0);
        hosts.Clock.Now = initial.AddHours(24); await RoundAsync(2, 1);
        hosts.Clock.Now = initial.AddHours(24).AddTicks(10); await RoundAsync(2, 0);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        Assert.Equal(1, await db.ViewDeduplicationRecords.CountAsync(ct));
        Assert.Equal(initial.AddHours(24), (await db.ViewDeduplicationRecords.OrderByDescending(v => v.LastCountedAt).FirstAsync(ct)).LastCountedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CounterFailureOrOverflowRollsBackDedupAndAllowsRetry(bool overflow)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "view-rollback", ct); var id = pub.GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        if (overflow) { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET view_count={long.MaxValue} WHERE id={id}", ct); }
        else { await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION publishing.reject_views() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$; CREATE TRIGGER reject_views BEFORE UPDATE OF view_count ON publishing.publications FOR EACH ROW EXECUTE FUNCTION publishing.reject_views()", ct); }
        using (var failed = await ViewAsync(author, id, null, ct)) { Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); }
        Assert.Equal(0, await db.ViewDeduplicationRecords.CountAsync(ct));
        Assert.Equal(overflow ? long.MaxValue : 0, (await db.Publications.AsNoTracking().SingleAsync(ct)).ViewCount);
        if (!overflow) { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_views ON publishing.publications; DROP FUNCTION publishing.reject_views()", ct); }
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET view_count=0 WHERE id={id}", ct);
        Assert.True((await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
    }
}
