using BlogEngine.Api.Contracts.Publishing;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.ReadModels;

namespace BlogEngine.Api.Contracts.Reading;

public sealed record PublicationDto(Guid Id, string Title, string Slug, string? Description, string? CoverUrl, AuthorRef Author,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset FirstPublishedAt, IReadOnlyList<TaxonomyDto> Categories,
    IReadOnlyList<TaxonomyDto> Tags, long LikeCount, long ViewCount, bool IsLikedByCurrentUser, string Markdown)
{
    public static PublicationDto From(ClassifiedPublication value)
    {
        var p = value.Publication;
        return new(p.Id, p.Title, p.Slug, p.Description, p.CoverUrl, new(p.AuthorId), p.CreatedAt, p.UpdatedAt, p.FirstPublishedAt!.Value,
            value.Categories.Select(TaxonomyDto.From).ToArray(), value.Tags.Select(TaxonomyDto.From).ToArray(), p.LikeCount, p.ViewCount, p.IsLikedByCurrentUser, p.Markdown!);
    }
}
