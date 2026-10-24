using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.ReadModels;

public enum PublicationSort { Recent, Popular }

public sealed record PublicationFeedRequest
{
    public PublicationFeedRequest(PageRequest page, PublicationSort sort, IReadOnlyList<Guid> categoryIds, IReadOnlyList<Guid> tagIds)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Enum.IsDefined(sort)) { throw DomainError.Validation("sort"); }
        Page = page; Sort = sort;
        CategoryIds = Filter(categoryIds, 5, "categoryIds"); TagIds = Filter(tagIds, 20, "tagIds");
    }
    public PageRequest Page { get; }
    public PublicationSort Sort { get; }
    public IReadOnlyList<Guid> CategoryIds { get; }
    public IReadOnlyList<Guid> TagIds { get; }

    private static IReadOnlyList<Guid> Filter(IReadOnlyList<Guid> ids, int maximum, string field)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > maximum || ids.Distinct().Count() != ids.Count) { throw DomainError.Validation(field); }
        return Array.AsReadOnly(ids.ToArray());
    }
}

public sealed record PublicationFeed(Guid Id, string Title, string Slug, string? Description, string? CoverUrl, AuthorRef Author,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset FirstPublishedAt, IReadOnlyList<TaxonomyItem> Categories,
    IReadOnlyList<TaxonomyItem> Tags, long LikeCount, long ViewCount, bool IsLikedByCurrentUser);
