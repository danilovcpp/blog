using BlogEngine.Api.Contracts.Publishing;
using BlogEngine.Core.ReadModels;

namespace BlogEngine.Api.Contracts.Reading;

public sealed record PublicationCardDto(Guid Id, string Title, string Slug, string? Description, string? CoverUrl, AuthorRef Author,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset FirstPublishedAt, IReadOnlyList<TaxonomyDto> Categories,
    IReadOnlyList<TaxonomyDto> Tags, long LikeCount, long ViewCount, bool IsLikedByCurrentUser)
{
    public static PublicationCardDto From(PublicationFeed item) => new(item.Id, item.Title, item.Slug, item.Description, item.CoverUrl,
        item.Author, item.CreatedAt, item.UpdatedAt, item.FirstPublishedAt, item.Categories.Select(TaxonomyDto.From).ToArray(),
        item.Tags.Select(TaxonomyDto.From).ToArray(), item.LikeCount, item.ViewCount, item.IsLikedByCurrentUser);
}
