using BlogEngine.Core.Publishing.Domain;

namespace BlogEngine.Api.Contracts.Publishing;

public sealed record PublicationWrite(string Title, string Slug, string Markdown, Guid[] CategoryIds, Guid[] TagIds,
    string? Description = null, string? CoverUrl = null)
{
    public PublicationContent ToContent() => new(Title, Slug, Markdown, Description, CoverUrl, CategoryIds, TagIds);
}
