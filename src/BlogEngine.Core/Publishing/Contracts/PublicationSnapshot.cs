using BlogEngine.Core.Publishing.Domain;

namespace BlogEngine.Core.Publishing.Contracts;

public sealed record PublicationSnapshot(Guid Id, string Title, string Slug, string? Markdown, string? Description, string? CoverUrl,
    Guid AuthorId, PublicationState State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? FirstPublishedAt,
    Guid Version, long LikeCount, long ViewCount, bool IsLikedByCurrentUser);
public sealed record ClassifiedPublication(PublicationSnapshot Publication, IReadOnlyList<TaxonomyItem> Categories, IReadOnlyList<TaxonomyItem> Tags);
