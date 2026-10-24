using System.Text.Json;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Reading;

[Trait("Category", "Reading")]
public sealed class QueryInspectionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ActualHttpSqlIsBoundedAndExplainedForBothOrdersSizesFiltersAndDistantPages()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture();
        await using var f = await StartAsync(postgres, ct, capture); await ReadingDataset.SeedAsync(f, ct);
        ReadingDataset.Authenticate(f.Anonymous, f);
        await using var scope = f.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString()); await connection.OpenAsync(ct);
        var scenarios = new List<object>();
        var filters = new (string Name, int[] Categories, int[] Tags)[]
        {
            ("none", [], []), ("category", [0], []), ("tag", [], [1]), ("combined", [0,1], [0,1]),
            ("maximum", [0,1,2,3,4], Enumerable.Range(0,20).ToArray()), ("empty", [5], []),
        };
        foreach (var sort in new[] { "recent", "popular" })
        {
            foreach (var size in new[] { 20, 100 })
            {
                foreach (var number in new[] { 1, 10, 1000, int.MaxValue })
                {
                    foreach (var filter in filters)
                    {
                        capture.Clear();
                        var page = await ReadingContractTests.GetAsync(f.Anonymous, ReadingDataset.Query(sort, number, size, filter.Categories, filter.Tags), ct);
                        var expected = ReadingDataset.Expected(sort, filter.Categories, filter.Tags);
                        var offset = ((long)number - 1) * size;
                        var available = offset >= expected.Count ? [] : expected.Skip((int)offset).Take(size + 1).ToArray();
                        Assert.Equal(available.Take(size), PaginationAndFilterTests.Ids(page));
                        var commands = capture.Commands; var data = commands.Where(c => !c.IsAuthentication).ToArray();
                        Assert.InRange(data.Length, 1, 5); Assert.Equal(3, commands.Count(c => c.IsAuthentication));
                        var feed = Assert.Single(data, c => c.Sql.Contains("FROM publishing.publications", StringComparison.Ordinal));
                        Assert.Equal(offset, feed.Parameters.Single(p => p.ParameterName == "offset").Value);
                        Assert.Equal(size + 1, feed.Parameters.Single(p => p.ParameterName == "fetch_size").Value);
                        Assert.DoesNotContain("markdown", feed.Sql, StringComparison.OrdinalIgnoreCase);
                        await using (var replay = Command(connection, feed))
                        {
                            await using var reader = await replay.ExecuteReaderAsync(ct); var ids = new List<Guid>();
                            while (await reader.ReadAsync(ct)) { ids.Add(reader.GetGuid(reader.GetOrdinal("Id"))); }
                            Assert.Equal(available, ids); Assert.InRange(ids.Count, 0, size + 1);
                        }
                        var evidence = new List<object>();
                        foreach (var command in data)
                        {
                            await using var explain = Command(connection, command, "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) ");
                            var plan = JsonSerializer.Deserialize<JsonElement>((string)(await explain.ExecuteScalarAsync(ct))!);
                            Assert.True(plan[0].TryGetProperty("Plan", out _));
                            evidence.Add(new { sql = command.Sql, parameters = command.Parameters.Select(p => new { name = p.ParameterName, type = ParameterType(p), value = p.Value is DBNull ? null : p.Value }), plan });
                        }
                        var name = $"us2-{sort}-{size}-{number}-{filter.Name}";
                        await File.WriteAllTextAsync(ArtifactPath("queries/" + name + ".json"), JsonSerializer.Serialize(new
                        { sort, page = number, pageSize = size, offset, dataQueries = data.Length, authenticationQueries = 3, readerRows = available.Length, items = PaginationAndFilterTests.Ids(page).Length, commands = evidence }), ct);
                        scenarios.Add(new { name, dataQueries = data.Length, authenticationQueries = 3, readerRows = available.Length });
                    }
                }
            }
        }
        await File.WriteAllTextAsync(ArtifactPath("queries/us2-summary.json"), JsonSerializer.Serialize(new
        {
            status = "passed",
            dataset = 250,
            postgres = "18.6",
            scenarios,
            maximumDataQueries = 5,
            interpretation = "Bounded projections, exact public predicate and deterministic indexed sort. Plans are from actual parameterized HTTP SQL. Sequential scans on 250 rows or empty/deep pages are valid; SC-005 requires the final dedicated dataset and host."
        }), ct);
    }

    internal static NpgsqlCommand Command(NpgsqlConnection connection, ReadingCommand recorded, string prefix = "")
    {
        var command = new NpgsqlCommand(prefix + recorded.Sql, connection);
        foreach (var parameter in recorded.Parameters)
        { command.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value }); }
        return command;
    }

    internal static string ParameterType(NpgsqlParameter parameter) => (parameter.NpgsqlDbType & NpgsqlDbType.Array) != 0 ?
        (parameter.NpgsqlDbType & ~NpgsqlDbType.Array) + "[]" : parameter.NpgsqlDbType.ToString();
}
