using System.Data.Common;
using System.Net;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Engagement.Application;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Engagement;
using BlogEngine.Infrastructure.Persistence.Publishing;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Http;
using BlogEngine.IntegrationTests.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.CommentConcurrencyTests;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Engagement.LikeContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Engagement;

[Trait("Category", "Likes")]
[Trait("Category", "Concurrency")]
public sealed class LikeConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ReusedContextKeepsCountersCoherentWithoutChangingContent()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-context", ct); var id = pub.GetProperty("id").GetGuid();
        await using var scope = f.Factory.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<LikeService>();
        var actor = new CurrentActor(pub.GetProperty("author").GetProperty("id").GetGuid(), []);
        Assert.Equal(1, (await service.SetAsync(actor, id, true, ct)).LikeCount);
        Assert.Equal(1, (await service.SetAsync(actor, id, true, ct)).LikeCount);
        Assert.Equal(0, (await service.SetAsync(actor, id, false, ct)).LikeCount);
        var context = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var escaped = await scope.ServiceProvider.GetRequiredService<IPublicationWriteScope>().ExecuteAsync(id, (session, _) => Task.FromResult(session), ct);
        await using (var differentTransaction = await context.Database.BeginTransactionAsync(ct))
        { await Assert.ThrowsAsync<InvalidOperationException>(() => escaped.AdjustCountersAsync(new CounterDelta(1, 0), ct)); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertSourceAsync(db, id, 0, ct);
        Assert.Equal(pub.GetProperty("version").GetGuid(), (await db.Publications.AsNoTracking().SingleAsync(ct)).Version);
    }

    [Fact]
    public async Task HundredSameUserRequestsAndMixedHundredUsersAcrossTwoHostsMatchSource()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture();
        await using var f = await StartAsync(postgres, ct, capture); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-concurrent", ct); var id = pub.GetProperty("id").GetGuid();
        await using var second = new ApiFactory(f.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BlogEngine.Api.Configuration.ApiOptions>>().Value.ConnectionString, f.Identity);
        using var other = second.CreateClient(); other.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var barrier = new ConcurrencyBarrier(100);
        async Task PutAsync(int number)
        {
            await barrier.SignalAndWaitAsync(ct);
            var result = await JsonAsync(await LikeAsync(number % 2 == 0 ? author : other, id, true, ct), HttpStatusCode.OK, ct);
            Assert.Equal(1, result.GetProperty("likeCount").GetInt64()); Assert.True(result.GetProperty("isLikedByCurrentUser").GetBoolean());
        }
        await Task.WhenAll(Enumerable.Range(0, 100).Select(PutAsync));
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertSourceAsync(db, id, 1, ct);
        var created = (await db.Likes.AsNoTracking().SingleAsync(ct)).CreatedAt;
        await JsonAsync(await LikeAsync(other, id, true, ct), HttpStatusCode.OK, ct);
        Assert.Equal(created, (await db.Likes.AsNoTracking().SingleAsync(ct)).CreatedAt);

        var clients = new List<HttpClient>(); var rounds = new List<object>();
        try
        {
            for (var i = 0; i < 100; i++)
            {
                var client = await f.ActorAsync(LocalRole.Reader, ct);
                if (i % 2 == 1) { var replica = second.CreateClient(); replica.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization; client = replica; clients.Add(replica); }
                else { clients.Add(client); }
            }
            for (var round = 0; round < 3; round++)
            {
                var start = new ConcurrencyBarrier(100);
                async Task ChangeAsync(int i)
                {
                    await start.SignalAndWaitAsync(ct); var desired = (i + round) % 3 != 0;
                    // The same pair also races with the opposite desired state; final explicit state makes the total independently predictable.
                    var racing = await Task.WhenAll(LikeAsync(clients[i], id, desired, ct), LikeAsync(clients[i], id, !desired, ct));
                    foreach (var response in racing) { using (response) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); } }
                    await JsonAsync(await LikeAsync(clients[i], id, desired, ct), HttpStatusCode.OK, ct);
                    await JsonAsync(await LikeAsync(clients[i], id, desired, ct), HttpStatusCode.OK, ct);
                }
                await Task.WhenAll(Enumerable.Range(0, 100).Select(ChangeAsync));
                var expected = 1 + Enumerable.Range(0, 100).Count(i => (i + round) % 3 != 0);
                await AssertSourceAsync(db, id, expected, ct); rounds.Add(new { round, expected, actual = await db.Likes.LongCountAsync(ct) });
            }
        }
        finally { foreach (var client in clients) { client.Dispose(); } }
        var after = await db.Publications.AsNoTracking().SingleAsync(ct);
        Assert.Equal(pub.GetProperty("version").GetGuid(), after.Version); Assert.Equal(0, after.ViewCount);
        capture.Clear(); await JsonAsync(await LikeAsync(author, id, false, ct), HttpStatusCode.OK, ct);
        var commands = capture.Commands; var data = commands.Where(c => !c.IsAuthentication).ToArray();
        Assert.Contains(data, c => c.Sql.Contains("FOR UPDATE", StringComparison.Ordinal));
        Assert.Contains(data, c => c.Sql.Contains("DELETE FROM engagement.likes", StringComparison.Ordinal));
        Assert.Contains(data, c => c.Sql.Contains("UPDATE publishing.publications SET like_count", StringComparison.Ordinal));
        await AssertSourceAsync(db, id, 67, ct);
        await File.WriteAllTextAsync(ArtifactPath("us6-likes.json"), JsonSerializer.Serialize(new
        {
            sameUserConcurrentPuts = 100,
            hosts = 2,
            mixedUsers = 100,
            rounds,
            sourceEqualsAggregate = true,
            contentVersionUnchanged = true,
            dataQueries = data.Length,
            authenticationQueries = commands.Count(c => c.IsAuthentication),
            sql = data.Select(c => c.Sql),
        }), ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureBetweenSourceAndCounterRollsBackAndRetrySucceeds(bool desired)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-rollback", ct); var id = pub.GetProperty("id").GetGuid();
        if (!desired) { await JsonAsync(await LikeAsync(author, id, true, ct), HttpStatusCode.OK, ct); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION publishing.reject_likes() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$; CREATE TRIGGER reject_likes BEFORE UPDATE OF like_count ON publishing.publications FOR EACH ROW EXECUTE FUNCTION publishing.reject_likes()", ct);
        using (var failed = await LikeAsync(author, id, desired, ct)) { await InfrastructureContractTests.AssertProblem(failed, 500, "internal_error"); }
        await AssertSourceAsync(db, id, desired ? 0 : 1, ct);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_likes ON publishing.publications; DROP FUNCTION publishing.reject_likes()", ct);
        await JsonAsync(await LikeAsync(author, id, desired, ct), HttpStatusCode.OK, ct); await AssertSourceAsync(db, id, desired ? 1 : 0, ct);
        Assert.DoesNotContain(f.Factory.Logs.Entries, e => e.Message.Contains("fixture failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommitFailureRollsBackSourceAndAlreadyWrittenCounter()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-commit", ct); var id = pub.GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION engagement.reject_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$; CREATE CONSTRAINT TRIGGER reject_commit AFTER INSERT ON engagement.likes DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION engagement.reject_commit()", ct);
        using var failed = await LikeAsync(author, id, true, ct); await InfrastructureContractTests.AssertProblem(failed, 500, "internal_error");
        await AssertSourceAsync(db, id, 0, ct);
    }

    [Fact]
    public async Task RuntimeCannotRewriteLikesAndOperatorHasNoEngagementAccess()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        foreach (var (role, statements) in new[]
        {
            ("runtime_role", new[] { "UPDATE engagement.likes SET created_at=now()", "TRUNCATE engagement.likes", "CREATE TABLE engagement.forbidden(id int)" }),
            ("operator_role", new[] { "SELECT * FROM engagement.likes", "INSERT INTO engagement.likes VALUES(gen_random_uuid(),gen_random_uuid(),now())", "DELETE FROM engagement.likes" }),
        })
        {
            await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct);
            await using (var setRole = new NpgsqlCommand($"SET ROLE \"{f.Roles[role]}\"", connection)) { await setRole.ExecuteNonQueryAsync(ct); }
            foreach (var sql in statements)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState);
            }
        }
    }

    [Fact]
    public async Task Sc003IndependentWalkthroughUsesTwoHostsAndRetriesAfterRollback()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-walkthrough", ct); var id = pub.GetProperty("id").GetGuid();
        await using var second = new ApiFactory(f.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BlogEngine.Api.Configuration.ApiOptions>>().Value.ConnectionString, f.Identity);
        using var replica = second.CreateClient(); replica.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var barrier = new ConcurrencyBarrier(100);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            await barrier.SignalAndWaitAsync(ct); await JsonAsync(await LikeAsync(i % 2 == 0 ? author : replica, id, true, ct), HttpStatusCode.OK, ct);
        }));
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertSourceAsync(db, id, 1, ct);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION publishing.fail_walkthrough() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$; CREATE TRIGGER fail_walkthrough BEFORE UPDATE OF like_count ON publishing.publications FOR EACH ROW EXECUTE FUNCTION publishing.fail_walkthrough()", ct);
        using (var failed = await LikeAsync(replica, id, false, ct)) { Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); }
        await AssertSourceAsync(db, id, 1, ct);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_walkthrough ON publishing.publications; DROP FUNCTION publishing.fail_walkthrough()", ct);
        await JsonAsync(await LikeAsync(author, id, false, ct), HttpStatusCode.OK, ct);
        await JsonAsync(await LikeAsync(replica, id, false, ct), HttpStatusCode.OK, ct); await AssertSourceAsync(db, id, 0, ct);
        var closed = await JsonAsync(await SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/unpublish", pub.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        using (var hidden = await LikeAsync(replica, id, true, ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        using (var deleted = await SendAsync(author, HttpMethod.Delete, $"/api/v1/publications/{id}", closed.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        using (var hidden = await LikeAsync(replica, id, false, ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        using (var hidden = await f.Anonymous.GetAsync($"/api/v1/publications/{id}/counters", ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        await AssertSourceAsync(db, id, 0, ct);
        await File.WriteAllTextAsync(ArtifactPath("us6-concurrency.md"), """
            # US6 / SC-003 independent HTTP walkthrough

            Run: dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~Sc003IndependentWalkthrough

            Disposable PostgreSQL 18.6; two independent ApiFactory service providers and real runtime credentials/JwtBearer; one shared database.

            1. Author POST publication + POST publish: 201/200, original edit version retained for later close.
            2. Barrier releases 100 bodyless PUT /api/v1/publications/{id}/like requests across both hosts with the same Bearer: all 200; source=aggregate=1.
            3. Inject PostgreSQL BEFORE UPDATE OF like_count failure. Host 2 DELETE: 500; source and aggregate both remain 1.
            4. Remove failure; host 1 DELETE and host 2 repeated DELETE: 200/200; source=aggregate=0.
            5. POST unpublish using original edit version: 200; host 2 PUT: 404. DELETE publication: 204; host 2 DELETE like and anonymous GET counters: 404.
            6. Final persisted source=aggregate=0; no child reaction is publicly exposed after close/deletion.

            Mixed 100-user desired/opposite/repeated writes and exact per-round aggregate checks across two hosts are independently recorded by HundredSameUserRequestsAndMixedHundredUsersAcrossTwoHostsMatchSource in us6-likes.json.
            Both deterministic close/like lock orders, overflow/underflow, deferred commit failure and cancellation are covered by LikeConcurrencyTests, without sleeps.
            """, ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BigintOverflowAndUnderflowRollbackSource(bool overflow)
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-bigint", ct); var id = pub.GetProperty("id").GetGuid();
        if (!overflow) { await JsonAsync(await LikeAsync(author, id, true, ct), HttpStatusCode.OK, ct); }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var count = overflow ? long.MaxValue : 0;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET like_count={count} WHERE id={id}", ct);
        using var failed = await LikeAsync(author, id, overflow, ct); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(overflow ? 0 : 1, await db.Likes.CountAsync(ct)); Assert.Equal(count, (await db.Publications.AsNoTracking().SingleAsync(ct)).LikeCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task PublicationCloseWinnerIsRecheckedAfterLock(bool desired, bool delete)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-close", ct); var id = pub.GetProperty("id").GetGuid();
        if (!desired) { await JsonAsync(await LikeAsync(author, id, true, ct), HttpStatusCode.OK, ct); }
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand(delete ? "UPDATE publishing.publications SET deleted_at=now() WHERE id=@id" : "UPDATE publishing.publications SET state=0 WHERE id=@id", connection, tx))
        { command.Parameters.AddWithValue("id", id); await command.ExecuteNonQueryAsync(ct); }
        var pending = LikeAsync(author, id, desired, ct); await WaitForBlockedAsync(f.Database.ConnectionString, pending, ct); await tx.CommitAsync(ct);
        using var denied = await pending; await InfrastructureContractTests.AssertProblem(denied, 404, "resource_not_found");
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertSourceAsync(db, id, desired ? 0 : 1, ct);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task LikeWinnerCommitsBeforeWaitingCloseAndChildBecomesUnavailable(bool desired, bool delete)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        var gate = new CounterGate(); await using var f = await StartAsync(postgres, ct, gate); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-first", ct); var id = pub.GetProperty("id").GetGuid();
        if (!desired) { await JsonAsync(await LikeAsync(author, id, true, ct), HttpStatusCode.OK, ct); }
        gate.Arm(); var like = LikeAsync(author, id, desired, ct); await gate.Entered.Task.WaitAsync(ct);
        var close = SendAsync(author, delete ? HttpMethod.Delete : HttpMethod.Post, $"/api/v1/publications/{id}" + (delete ? "" : "/unpublish"), pub.GetProperty("version").GetGuid(), null, ct);
        try { await WaitForBlockedAsync(f.Database.ConnectionString, close, ct); }
        finally { gate.Release.TrySetResult(); }
        using var liked = await like; Assert.Equal(HttpStatusCode.OK, liked.StatusCode);
        using var closed = await close; Assert.Equal(delete ? HttpStatusCode.NoContent : HttpStatusCode.OK, closed.StatusCode);
        using var inaccessible = await LikeAsync(author, id, !desired, ct); Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        using var counters = await author.GetAsync($"/api/v1/publications/{id}/counters", ct); Assert.Equal(HttpStatusCode.NotFound, counters.StatusCode);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); await AssertSourceAsync(db, id, desired ? 1 : 0, ct);
    }

    [Fact]
    public async Task CancellationAfterSourceWriteRollsBackEntireScope()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var f = await StartAsync(postgres, ct); var author = await f.ActorAsync(LocalRole.Author, ct);
        var pub = await PublishedAsync(author, "like-cancel", ct); var id = pub.GetProperty("id").GetGuid();
        var gate = new CounterGate(); gate.Arm(); await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(gate).Options);
        var service = new LikeService(new LikeWriteStore(db, new PublicationWriteScope(db)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = service.SetAsync(new CurrentActor(pub.GetProperty("author").GetProperty("id").GetGuid(), []), id, true, cancellation.Token);
        await gate.Entered.Task.WaitAsync(ct); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await using var verify = BootstrapTests.Context(f.Database.ConnectionString); await AssertSourceAsync(verify, id, 0, ct);
    }

    internal static async Task AssertSourceAsync(BlogDbContext db, Guid id, long expected, CancellationToken ct)
    {
        Assert.Equal(expected, await db.Likes.LongCountAsync(l => l.PublicationId == id, ct));
        Assert.Equal(expected, (await db.Publications.AsNoTracking().SingleAsync(p => p.Id == id, ct)).LikeCount);
    }

    private sealed class CounterGate : DbCommandInterceptor
    {
        private int armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE publishing.publications SET like_count", StringComparison.Ordinal) && Interlocked.Exchange(ref armed, 0) == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
}
