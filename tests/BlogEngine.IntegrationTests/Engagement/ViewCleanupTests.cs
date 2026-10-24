using System.Net;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Infrastructure.Maintenance;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.CommentConcurrencyTests;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Engagement.ViewContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Engagement;

[Trait("Category", "Views")]
[Trait("Category", "Operations")]
[Trait("Category", "Concurrency")]
public sealed class ViewCleanupTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task CleanupQueriesAreBoundedIndexedAndLockPublicationsBeforeDedupInUuidOrder()
    {
        var ct = TestContext.Current.CancellationToken; await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct);
        var ids = new[] { (await PublishedAsync(author, "cleanup-a", ct)).GetProperty("id").GetGuid(), (await PublishedAsync(author, "cleanup-b", ct)).GetProperty("id").GetGuid() };
        await using var seed = BootstrapTests.Context(f.Database.ConnectionString);
        foreach (var id in ids)
        { await seed.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO engagement.view_deduplication_records SELECT {id},2,sha256(int8send(i)),{hosts.Clock.Now.AddHours(-24)} FROM generate_series(1,1000) AS i", ct); }
        await seed.Database.ExecuteSqlRawAsync("ANALYZE engagement.view_deduplication_records", ct);
        var capture = new ReadingSqlCapture();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(capture).Options);
        Assert.Equal(500, await new ViewDeduplicationCleanup(db, hosts.Clock).RunBatchAsync(ct)); Assert.Empty(db.ChangeTracker.Entries());
        var commands = capture.Commands; Assert.Equal(3, commands.Count);
        Assert.Contains("LIMIT", commands[0].Sql, StringComparison.Ordinal); Assert.DoesNotContain("FOR UPDATE", commands[0].Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY id FOR UPDATE", commands[1].Sql, StringComparison.Ordinal);
        Assert.StartsWith("DELETE FROM engagement.view_deduplication_records", commands[2].Sql);
        Assert.Contains("last_counted_at <=", commands[2].Sql, StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct);
        var candidate = commands[0]; await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + candidate.Sql, connection);
        foreach (var parameter in candidate.Parameters) { explain.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value }); }
        var plan = (string)(await explain.ExecuteScalarAsync(ct))!;
        Assert.Contains("ix_view_deduplication_expiry", plan, StringComparison.Ordinal);
        await File.WriteAllTextAsync(ArtifactPath("queries/us7-cleanup.json"), JsonSerializer.Serialize(new { batchSize = 500, queries = commands.Select(c => c.Sql), plan = JsonSerializer.Deserialize<JsonElement>(plan), tracking = 0 }), ct);
    }

    [Fact]
    public async Task PublicationLockWaitHasBoundedDatabaseTimeoutAndRetainsExpiredRecord()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var id = (await PublishedAsync(author, "cleanup-timeout", ct)).GetProperty("id").GetGuid();
        await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct); hosts.Clock.Now = hosts.Clock.Now.AddHours(24);
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand("SELECT id FROM publishing.publications WHERE id=@id FOR UPDATE", connection, tx)) { command.Parameters.AddWithValue("id", id); await command.ExecuteScalarAsync(ct); }
        await using var scope = hosts.Second.Services.CreateAsyncScope(); var cleanup = scope.ServiceProvider.GetRequiredService<ViewDeduplicationCleanup>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.RunBatchAsync(ct));
        Assert.IsType<TimeoutException>(Assert.IsType<NpgsqlException>(error.InnerException).InnerException);
        Assert.InRange(started.Elapsed.TotalSeconds, 0, 15); await tx.RollbackAsync(ct);
        await using var verify = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(1, await verify.ViewDeduplicationRecords.CountAsync(ct)); Assert.Equal(1, (await verify.Publications.SingleAsync(ct)).ViewCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothCleanupRegistrationOrdersPreserveWindow(bool cleanupFirst)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var id = (await PublishedAsync(author, "cleanup-race", ct)).GetProperty("id").GetGuid();
        var token = (await JsonAsync(await ViewAsync(f.Anonymous, id, null, ct), HttpStatusCode.OK, ct)).GetProperty("anonymousVisitorId").GetString();
        hosts.Clock.Now = hosts.Clock.Now.AddHours(24);
        var gate = new ViewCommandGate(sql => cleanupFirst ? sql.StartsWith("DELETE FROM engagement.view_deduplication_records", StringComparison.Ordinal) : sql.Contains("ORDER BY id FOR UPDATE", StringComparison.Ordinal)); gate.Arm();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(gate).Options);
        var cleanup = new ViewDeduplicationCleanup(db, hosts.Clock); var pending = cleanup.RunBatchAsync(ct); await gate.Entered.Task.WaitAsync(ct);
        var register = ViewAsync(hosts.Replica, id, token, ct);
        if (cleanupFirst)
        { try { await WaitForBlockedAsync(f.Database.ConnectionString, register, ct); } finally { gate.Release.TrySetResult(); } }
        else
        { Assert.True((await JsonAsync(await register, HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean()); gate.Release.TrySetResult(); }
        Assert.Equal(cleanupFirst ? 1 : 0, await pending);
        if (cleanupFirst) { Assert.True((await JsonAsync(await register, HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean()); }
        Assert.False((await JsonAsync(await ViewAsync(f.Anonymous, id, token, ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
        await using var verify = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(1, await verify.ViewDeduplicationRecords.CountAsync(ct)); Assert.Equal(2, (await verify.Publications.SingleAsync(ct)).ViewCount);
    }

    [Fact]
    public async Task CleanupCancellationAndDeleteFailureRetainSourceAndAggregate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var id = (await PublishedAsync(author, "cleanup-cancel", ct)).GetProperty("id").GetGuid();
        await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct); hosts.Clock.Now = hosts.Clock.Now.AddHours(24);
        var gate = new ViewCommandGate(sql => sql.StartsWith("DELETE FROM engagement.view_deduplication_records", StringComparison.Ordinal)); gate.Arm();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(gate).Options);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new ViewDeduplicationCleanup(db, hosts.Clock).RunBatchAsync(cancel.Token); await gate.Entered.Task.WaitAsync(ct); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await using var verify = BootstrapTests.Context(f.Database.ConnectionString);
        Assert.Equal(1, await verify.ViewDeduplicationRecords.CountAsync(ct)); Assert.Equal(1, (await verify.Publications.SingleAsync(ct)).ViewCount);
        await verify.Database.ExecuteSqlRawAsync("CREATE FUNCTION engagement.reject_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$; CREATE TRIGGER reject_cleanup BEFORE DELETE ON engagement.view_deduplication_records FOR EACH ROW EXECUTE FUNCTION engagement.reject_cleanup()", ct);
        await using var scope = hosts.Second.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => scope.ServiceProvider.GetRequiredService<ViewDeduplicationCleanup>().RunBatchAsync(ct));
        Assert.Equal(1, await verify.ViewDeduplicationRecords.CountAsync(ct)); Assert.Equal(1, (await verify.Publications.SingleAsync(ct)).ViewCount);
    }

    [Fact]
    public async Task TwoWorkersKeepFreshRecordsAndNeverDecreaseAggregate()
    {
        var ct = TestContext.Current.CancellationToken; await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var pub = await PublishedAsync(author, "cleanup-batch", ct); var id = pub.GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO engagement.view_deduplication_records SELECT {id},2,sha256(int8send(i)),{hosts.Clock.Now.AddHours(-24)} FROM generate_series(1,1101) AS i", ct);
        await using var first = f.Factory.Services.CreateAsyncScope(); await using var second = hosts.Second.Services.CreateAsyncScope();
        var a = first.ServiceProvider.GetRequiredService<ViewDeduplicationCleanup>(); var b = second.ServiceProvider.GetRequiredService<ViewDeduplicationCleanup>();
        var deleted = await Task.WhenAll(a.RunBatchAsync(ct), b.RunBatchAsync(ct));
        Assert.All(deleted, n => Assert.InRange(n, 0, 500));
        while (await a.RunBatchAsync(ct) != 0) { }
        Assert.Equal(0, await db.ViewDeduplicationRecords.CountAsync(ct));
        Assert.True((await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
        hosts.Clock.Now = hosts.Clock.Now.AddHours(24).AddTicks(-10);
        Assert.Equal(0, await b.RunBatchAsync(ct));
        Assert.False((await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
        hosts.Clock.Now = hosts.Clock.Now.AddTicks(10); Assert.Equal(1, await b.RunBatchAsync(ct));
        Assert.True((await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
        Assert.Equal(2, (await db.Publications.SingleAsync(ct)).ViewCount);
    }
}
