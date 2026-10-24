using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.Performance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlogEngine.IntegrationTests.Operations;

[Trait("Category", "Operations")]
public sealed class BenchmarkPreparationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public void NearestRankIncludesFailedAndSlowAttemptsAndRejectsEmptySamples()
    {
        var template = new RequestTemplate("feed", "recent", 20, 1, "none", "none", "nonempty", false, false, 0);
        var samples = Enumerable.Range(0, 100).Select(i => new Attempt(i, i == 99 ? 5000 : i + 1, 20, i == 99 ? "timeout" : null, false, template, false)).ToArray();
        var stats = ReadBenchmark.Summarize(samples, 10);
        Assert.Equal(95, stats.P95); Assert.Equal(99, stats.P99); Assert.Equal(5000, stats.Max); Assert.Equal(1, stats.Failures); Assert.True(stats.Passed);
        Assert.False(ReadBenchmark.Summarize(samples.Append(samples[^1]), 10).Passed);
        Assert.False(ReadBenchmark.Summarize([], 10).Passed);
    }

    [Fact]
    public async Task SeedRejectsAnUnmarkedDatabaseWithoutChangingRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        var generator = new SeedDataset("urn:benchmark-fixture");
        await Assert.ThrowsAsync<InvalidOperationException>(() => generator.SeedAsync(database.ConnectionString, "sc005_disposable_wrong", PublishingFixture.ArtifactPath("seed-safety"), ct));
        await using var db = BootstrapTests.Context(database.ConnectionString);
        Assert.Equal(0, await db.LocalUsers.CountAsync(ct)); Assert.Equal(0, await db.Publications.CountAsync(ct));
        var distribution = generator.VerifyDistribution(); Assert.Equal(1_000_000, distribution.GetProperty("likes").GetInt64());
        Assert.Equal(10_000_000, distribution.GetProperty("views").GetInt64());
        for (var n = 0; n < 2000; n++)
        { Assert.Equal(2048, System.Text.Encoding.UTF8.GetByteCount(SeedDataset.Body("body", n, 2048))); }
        Assert.Equal(SeedDataset.H("body", 7), SeedDataset.H("body", 7));
        Assert.NotEqual(SeedDataset.Id("user", 7), SeedDataset.Id("subject", 7));
    }
}
