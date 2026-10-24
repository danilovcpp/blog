using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Reading;

public sealed class PublicationFeedRequestTests
{
    [Fact]
    public void FiltersAreBoundedUniqueAndCopiedAndSortMustBeKnown()
    {
        var categories = Enumerable.Range(1, 5).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
        var tags = Enumerable.Range(1, 20).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
        var request = new PublicationFeedRequest(new PageRequest(int.MaxValue, 100), PublicationSort.Popular, categories, tags);
        var first = request.CategoryIds[0]; categories[0] = Guid.Empty;
        Assert.Equal(first, request.CategoryIds[0]); Assert.Equal(214748364600L, request.Page.Offset);
        foreach (var (categoryIds, tagIds, field) in new (Guid[], Guid[], string)[]
        {
            ([first, first], [], "categoryIds"), ([], [first, first], "tagIds"),
            (tags[..6], [], "categoryIds"), ([], [.. tags, Guid.Empty], "tagIds"),
        })
        { Assert.Equal(field, Assert.Throws<DomainError>(() => new PublicationFeedRequest(new(), PublicationSort.Recent, categoryIds, tagIds)).Field); }
        Assert.Equal("sort", Assert.Throws<DomainError>(() => new PublicationFeedRequest(new(), (PublicationSort)2, [], [])).Field);
    }
}
