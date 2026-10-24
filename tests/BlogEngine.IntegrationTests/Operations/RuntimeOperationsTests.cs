using System.Net;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Operations;

[Trait("Category", "Operations")]
public sealed class RuntimeOperationsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ProductionRuntimeStartsWithoutDdlAndDatabaseFailureRecoversWithinBounds()
    {
        var ct = TestContext.Current.CancellationToken; await using var f = await StartAsync(postgres, ct);
        await using var production = f.Factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production").ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Identity:JwksUrl"] = "https://benchmark.invalid/jwks" })));
        using var client = production.CreateClient();
        using (var live = await client.GetAsync("/health/live", ct)) { Assert.Equal(HttpStatusCode.OK, live.StatusCode); }
        using var scope = production.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        Assert.Equal(5, db.Database.GetCommandTimeout());
        await db.Database.OpenConnectionAsync(ct);
        await using (var ddl = db.Database.GetDbConnection().CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE publishing.must_be_forbidden(id int)";
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync(ct))).SqlState);
        }
        using var commandTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(30)", commandTimeout.Token));
        using (var live = await client.GetAsync("/health/live", ct)) { Assert.Equal(HttpStatusCode.OK, live.StatusCode); }
        Assert.DoesNotContain(f.Factory.Logs.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task ClientCancellationAtDatabaseBoundaryIsNotAnInternalError()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new ViewCommandGate(sql => sql.Contains("FROM publishing.publications", StringComparison.Ordinal));
        await using var f = await StartAsync(postgres, ct, gate);
        gate.Arm(); using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = f.Anonymous.GetAsync("/api/v1/publications", abort.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.DoesNotContain(f.Factory.Logs.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
        using var live = await f.Anonymous.GetAsync("/health/live", ct); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
