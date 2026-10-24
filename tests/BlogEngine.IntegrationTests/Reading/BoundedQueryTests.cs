using System.Data.Common;
using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Reading;

[Trait("Category", "Reading")]
public sealed class BoundedQueryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(20, false)]
    [InlineData(100, false)]
    [InlineData(20, true)]
    [InlineData(100, true)]
    public async Task FeedUsesAtMostFiveDataQueriesAndFixedAuthenticationQueries(int size, bool authenticated)
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture();
        await using var f = await StartAsync(postgres, ct, capture); await ReadingDataset.SeedAsync(f, ct);
        if (authenticated) { ReadingDataset.Authenticate(f.Anonymous, f); }
        foreach (var (categories, tags) in new (int[], int[])[] { ([0, 1], [0]), ([0, 1, 2, 3, 4], Enumerable.Range(0, 20).ToArray()) })
        {
            capture.Clear();
            var page = await ReadingContractTests.GetAsync(f.Anonymous, ReadingDataset.Query(pageSize: size, categories: categories, tags: tags), ct);
            var data = capture.Commands.Where(c => !c.IsAuthentication).ToArray();
            Assert.InRange(data.Length, 3, 5);
            Assert.Equal(authenticated ? 3 : 0, capture.Commands.Count(c => c.IsAuthentication));
            var feed = Assert.Single(data, c => c.Sql.Contains("FROM publishing.publications", StringComparison.Ordinal));
            Assert.DoesNotContain("markdown", feed.Sql, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("COUNT(", feed.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LIMIT", feed.Sql, StringComparison.Ordinal); Assert.Contains("OFFSET", feed.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT *", feed.Sql, StringComparison.OrdinalIgnoreCase);
            if (authenticated) { Assert.Contains("EXISTS", feed.Sql, StringComparison.Ordinal); Assert.Contains("engagement.likes", feed.Sql, StringComparison.Ordinal); }
            Assert.All(data, command => Assert.DoesNotContain("UPDATE ", command.Sql, StringComparison.Ordinal));
            if (PaginationAndFilterTests.Ids(page).Length != 0) { Assert.Equal(5, data.Length); }
        }
    }

    [Fact]
    public async Task FeedProjectsDtosWithoutTrackedEntitiesAndHonorsCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct); await ReadingDataset.SeedAsync(f, ct);
        await using var scope = f.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPublicFeedReader>();
        var result = await reader.ReadAsync(new(new PageRequest(1, 100), PublicationSort.Popular, [ReadingDataset.Category(0)], [ReadingDataset.Tag(0)]), ReadingDataset.FirstReaderId, ct);
        Assert.Equal(ReadingDataset.Expected("popular", [0], [0]).Take(100), result.Items.Select(p => p.Id));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.DoesNotContain(db.Model.GetEntityTypes().SelectMany(t => t.GetNavigations()), n => n.LazyLoadingEnabled);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new(new(), PublicationSort.Recent, [], []), null, canceled.Token));
    }
}

internal sealed record ReadingCommand(string Sql, NpgsqlParameter[] Parameters)
{
    public bool IsAuthentication => Sql.Contains("accounts.", StringComparison.Ordinal);
}

internal sealed class ReadingSqlCapture : DbCommandInterceptor
{
    private readonly List<ReadingCommand> commands = [];
    public IReadOnlyList<ReadingCommand> Commands { get { lock (commands) { return commands.ToArray(); } } }
    public void Clear() { lock (commands) { commands.Clear(); } }
    private void Capture(DbCommand command)
    {
        var parameters = command.Parameters.Cast<NpgsqlParameter>().Select(p => new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value }).ToArray();
        lock (commands) { commands.Add(new(command.CommandText, parameters)); }
    }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Capture(command); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Capture(command); return ValueTask.FromResult(result); }
}
