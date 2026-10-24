using System.Net;
using System.Text.Json;
using BlogEngine.IntegrationTests.Fixtures;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Reading;

[Trait("Category", "Reading")]
public sealed class PaginationAndFilterTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("recent", 20)]
    [InlineData("recent", 100)]
    [InlineData("popular", 20)]
    [InlineData("popular", 100)]
    public async Task StableDatasetCanBeTraversedWithoutRepeatsOrGaps(string sort, int size)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct); await ReadingDataset.SeedAsync(f, ct);
        foreach (var (categories, tags) in new (int[], int[])[] { ([], []), ([0, 1], [0, 1, 2]), ([5], []), ([], [20]), ([0, 1, 2, 3, 4], [0]) })
        {
            var actual = new List<Guid>(); var expected = ReadingDataset.Expected(sort, categories, tags);
            var pageNumber = 1;
            while (true)
            {
                var page = await ReadingContractTests.GetAsync(f.Anonymous, ReadingDataset.Query(sort, pageNumber, size, categories, tags), ct);
                Assert.Equal(pageNumber, page.GetProperty("page").GetInt32()); Assert.Equal(size, page.GetProperty("pageSize").GetInt32());
                var items = Ids(page); Assert.InRange(items.Length, 0, size); actual.AddRange(items);
                Assert.Equal(expected.Count > (long)pageNumber * size, page.GetProperty("hasNextPage").GetBoolean());
                if (!page.GetProperty("hasNextPage").GetBoolean()) { break; }
                Assert.True(pageNumber++ < 20);
            }
            Assert.Equal(expected, actual); Assert.Equal(actual.Count, actual.Distinct().Count());
            var beyond = await ReadingContractTests.GetAsync(f.Anonymous, ReadingDataset.Query(sort, pageNumber + 1, size, categories, tags), ct);
            Assert.Empty(Ids(beyond)); Assert.False(beyond.GetProperty("hasNextPage").GetBoolean());
        }
    }

    [Fact]
    public async Task DefaultsInt64OffsetLimitsAndInvalidFiltersFollowContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct); await ReadingDataset.SeedAsync(f, ct);
        var defaults = await ReadingContractTests.GetAsync(f.Anonymous, "/api/v1/publications", ct);
        Assert.Equal(1, defaults.GetProperty("page").GetInt32()); Assert.Equal(20, defaults.GetProperty("pageSize").GetInt32());
        Assert.Equal(ReadingDataset.Expected("recent").Take(20), Ids(defaults));
        foreach (var query in new[] { "page=2147483647&pageSize=100", "page=2147483647", "page=1000" })
        { var far = await ReadingContractTests.GetAsync(f.Anonymous, "/api/v1/publications?" + query, ct); Assert.Empty(Ids(far)); Assert.False(far.GetProperty("hasNextPage").GetBoolean()); }
        var max = await ReadingContractTests.GetAsync(f.Anonymous, ReadingDataset.Query(categories: [0, 1, 2, 3, 4], tags: Enumerable.Range(0, 20).ToArray()), ct);
        Assert.Empty(Ids(max));
        var invalid = new[]
        {
            "page=0", "page=-1", "page=2147483648", "page=", "page=1&page=2", "pageSize=0", "pageSize=101", "pageSize=20&pageSize=20",
            "sort=", "sort=Recent", "sort=unknown", "sort=popular&sort=recent", "state=Draft", "state=Published", "state=invalid",
            "categoryIds=", "tagIds=", "categoryIds=no", "tagIds=no", "categoryIds=" + ReadingDataset.Id(999), "tagIds=" + ReadingDataset.Id(999),
            "categoryIds=" + ReadingDataset.Category(0) + "&categoryIds=" + ReadingDataset.Category(0),
            "tagIds=" + ReadingDataset.Tag(0) + "&tagIds=" + ReadingDataset.Tag(0),
            string.Join('&', Enumerable.Range(0,6).Select(i => "categoryIds=" + ReadingDataset.Category(i))),
            string.Join('&', Enumerable.Range(0,21).Select(i => "tagIds=" + ReadingDataset.Tag(i))),
            "categoryIds=" + ReadingDataset.Category(0) + "," + ReadingDataset.Category(1), "authorId=" + ReadingDataset.AuthorId, "userId=" + ReadingDataset.FirstReaderId,
        };
        foreach (var query in invalid)
        {
            using var response = await f.Anonymous.GetAsync("/api/v1/publications?" + query, ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, query + ": " + response.StatusCode);
            await ReadingContractTests.AssertProblemAsync(response, "validation_failed", ct);
        }
        foreach (var path in new[] { "/api/v1/publications/no", "/api/v1/publications/no/counters" })
        { using var bad = await f.Anonymous.GetAsync(path, ct); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); }
    }

    internal static Guid[] Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
}
