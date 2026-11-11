using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.ReadModels;

namespace BlogEngine.Api.Contracts.Publishing;

public record WorkspaceCard(Guid Id, string Title, string Slug, string? Description, string? CoverUrl, AuthorRef Author,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? FirstPublishedAt, IReadOnlyList<TaxonomyDto> Categories,
    IReadOnlyList<TaxonomyDto> Tags, long LikeCount, long ViewCount, bool IsLikedByCurrentUser, PublicationState State, Guid Version, Guid? CoverFileId)
{
    public static WorkspaceCard From(ClassifiedPublication value)
    {
        var p = value.Publication;
        return new(p.Id, p.Title, p.Slug, p.Description, p.CoverUrl, new(p.AuthorId, p.AuthorAvatar), p.CreatedAt, p.UpdatedAt, p.FirstPublishedAt,
            value.Categories.Select(TaxonomyDto.From).ToArray(), value.Tags.Select(TaxonomyDto.From).ToArray(), p.LikeCount, p.ViewCount, p.IsLikedByCurrentUser, p.State, p.Version, p.CoverFileId);
    }
}

public sealed record WorkspacePublication : WorkspaceCard
{
    private WorkspacePublication(WorkspaceCard card, string markdown, PublicationMediaSnapshot media) : base(card)
    { Markdown = markdown; InlineImages = media.InlineImages; Attachments = media.Attachments; }
    public string Markdown { get; }
    public IReadOnlyList<PublicationInlineImage> InlineImages { get; }
    public IReadOnlyList<PublicationAttachment> Attachments { get; }
    public IReadOnlyList<Guid> InlineImageIds => InlineImages.Select(i => i.FileId).ToArray();
    public IReadOnlyList<Guid> AttachmentIds => Attachments.Select(i => i.FileId).ToArray();
    public static WorkspacePublication Detail(ClassifiedPublication value) => new(From(value), value.Publication.Markdown!, value.Media);
}
