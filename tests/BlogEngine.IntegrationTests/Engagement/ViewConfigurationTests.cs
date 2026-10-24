using System.Net;
using System.Threading.Channels;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Engagement.ViewContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Engagement;

[Trait("Category", "Views")]
[Trait("Category", "Operations")]
public sealed class ViewConfigurationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task WorkerWaitsFifteenMinutesRunsBoundedBatchAndStopsWithPendingTimer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        await using var hosts = await MultiHostFixture.StartAsync(postgres, ct); var f = hosts.First;
        var author = await f.ActorAsync(LocalRole.Author, ct); var id = (await PublishedAsync(author, "view-worker", ct)).GetProperty("id").GetGuid();
        await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct); hosts.Clock.Now = hosts.Clock.Now.AddHours(24);
        var time = new ManualScheduler();
        var logger = new CompletionLogger();
        using var worker = new ViewCleanupWorker(f.Factory.Services.GetRequiredService<IServiceScopeFactory>(), time, logger);
        await worker.StartAsync(ct); var first = await time.Timers.Reader.ReadAsync(ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(1, await db.ViewDeduplicationRecords.CountAsync(ct));
        Assert.Equal(TimeSpan.FromMinutes(15), first.DueTime); Assert.Equal(TimeSpan.FromMinutes(15), first.Period); first.Fire();
        await logger.Completed.Task.WaitAsync(ct);
        Assert.Equal(0, await db.ViewDeduplicationRecords.CountAsync(ct)); Assert.Equal(1, (await db.Publications.SingleAsync(ct)).ViewCount);
        await worker.StopAsync(ct); Assert.True(first.Disposed);
    }

    [Fact]
    public async Task WorkerStopCancelsInFlightBatchAndReleasesPublicationLocks()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        var clock = new ControlledDatabaseClock(); var gate = new ViewCommandGate(sql => sql.StartsWith("DELETE FROM engagement.view_deduplication_records", StringComparison.Ordinal));
        await using var f = await StartAsync(postgres, ct, gate, clock); var author = await f.ActorAsync(LocalRole.Author, ct);
        var id = (await PublishedAsync(author, "view-worker-stop", ct)).GetProperty("id").GetGuid();
        await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct); clock.Now = clock.Now.AddHours(24); gate.Arm();
        var time = new ManualScheduler(); using var worker = new ViewCleanupWorker(f.Factory.Services.GetRequiredService<IServiceScopeFactory>(), time, NullLogger<ViewCleanupWorker>.Instance);
        await worker.StartAsync(ct); (await time.Timers.Reader.ReadAsync(ct)).Fire(); await gate.Entered.Task.WaitAsync(ct);
        await worker.StopAsync(ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString); Assert.Equal(1, await db.ViewDeduplicationRecords.CountAsync(ct));
        Assert.True((await JsonAsync(await ViewAsync(author, id, null, ct), HttpStatusCode.OK, ct)).GetProperty("counted").GetBoolean());
    }

    [Fact]
    public async Task InvalidRingPreventsHostStartupAndNeverLeaksConfiguredValue()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        foreach (var (key, value) in new[] { ("ActiveKeyId", "missing"), ("Keys:fixture", "invalid-private-value"), ("Keys:bad.key", Convert.ToBase64String(new byte[32])) })
        {
            await using var invalid = f.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["AnonymousVisitors:" + key] = value })));
            var error = Assert.Throws<OptionsValidationException>(() => invalid.CreateClient());
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OpenApiDescribesOptionalBearerBodylessHeaderAndExactResult()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        var document = await JsonAsync(await f.Anonymous.GetAsync("/openapi/v1.json", ct), HttpStatusCode.OK, ct);
        var operation = document.GetProperty("paths").GetProperty("/api/v1/publications/{id}/views").GetProperty("post");
        Assert.Equal("registerView", operation.GetProperty("operationId").GetString()); Assert.False(operation.TryGetProperty("requestBody", out _));
        Assert.Equal(2, operation.GetProperty("security").GetArrayLength());
        Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "X-Anonymous-Visitor");
        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("ViewResult");
        Assert.Equal(3, schema.GetProperty("required").GetArrayLength()); Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    private sealed class ManualScheduler : TimeProvider
    {
        public Channel<ManualTimer> Timers { get; } = Channel.CreateUnbounded<ManualTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state, dueTime, period); Timers.Writer.TryWrite(timer); return timer; }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;
        public TimeSpan Period { get; } = period;
        public bool Disposed { get; private set; }
        public void Fire() { if (!Disposed) { callback(state); } }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class CompletionLogger : ILogger<ViewCleanupWorker>
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Information) { Completed.TrySetResult(); } }
    }
}
