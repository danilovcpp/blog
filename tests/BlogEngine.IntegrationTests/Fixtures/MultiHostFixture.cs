using BlogEngine.Api.Configuration;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.IntegrationTests.Fixtures;

internal sealed class MultiHostFixture : IAsyncDisposable
{
    private MultiHostFixture(PublishingFixture first, ApiFactory second, ControlledDatabaseClock clock)
    { First = first; Second = second; Clock = clock; Replica = second.CreateClient(); }
    public PublishingFixture First { get; }
    public ApiFactory Second { get; }
    public HttpClient Replica { get; }
    public ControlledDatabaseClock Clock { get; }
    public static async Task<MultiHostFixture> StartAsync(PostgresFixture postgres, CancellationToken ct)
    {
        var clock = new ControlledDatabaseClock();
        var first = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock);
        var connection = new NpgsqlConnectionStringBuilder(first.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString)
        { ApplicationName = "views-replica-" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var second = new ApiFactory(connection, first.Identity) { ViewClock = clock, VisitorKeys = first.Factory.VisitorKeys };
        return new(first, second, clock);
    }
    public async ValueTask DisposeAsync()
    {
        Replica.Dispose(); await Second.DisposeAsync(); await First.DisposeAsync();
    }
}

internal sealed class ControlledDatabaseClock : DatabaseClock
{
    private long ticks = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).Ticks;
    public DateTimeOffset Now { get => new(Interlocked.Read(ref ticks), TimeSpan.Zero); set => Interlocked.Exchange(ref ticks, value.UtcTicks); }
    public override Task<DateTimeOffset> ReadAsync(BlogDbContext db, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Now); }
}
