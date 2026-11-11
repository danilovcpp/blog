using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.Api.Contracts.Publishing;

public sealed record PublicationWrite(string Title, string Slug, string Markdown, Guid[] CategoryIds, Guid[] TagIds,
    string? Description = null, string? CoverUrl = null)
{
    private Guid? coverFileId;
    private Guid[]? inlineImageIds;
    private Guid[]? attachmentIds;
    private bool coverSpecified;
    private bool inlineSpecified;
    private bool attachmentsSpecified;
    public Guid? CoverFileId { get => coverFileId; init { coverFileId = value; coverSpecified = true; } }
    public Guid[]? InlineImageIds { get => inlineImageIds; init { inlineImageIds = value; inlineSpecified = true; } }
    public Guid[]? AttachmentIds { get => attachmentIds; init { attachmentIds = value; attachmentsSpecified = true; } }
    public PublicationContent ToContent()
    {
        if (inlineSpecified && InlineImageIds is null) { throw DomainError.Validation("inlineImageIds"); }
        if (attachmentsSpecified && AttachmentIds is null) { throw DomainError.Validation("attachmentIds"); }
        return new(Title, Slug, Markdown, Description, CoverUrl, CategoryIds, TagIds,
            new(coverSpecified, CoverFileId, InlineImageIds, AttachmentIds));
    }
}
