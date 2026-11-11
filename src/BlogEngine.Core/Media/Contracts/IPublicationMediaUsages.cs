namespace BlogEngine.Core.Media.Contracts;

public sealed record PublicationMediaChange(bool CoverSpecified = false, Guid? CoverFileId = null,
    IReadOnlyList<Guid>? InlineImageIds = null, IReadOnlyList<Guid>? AttachmentIds = null);

public sealed record PublicationInlineImage(Guid FileId, string Url, int Width, int Height);
public sealed record PublicationAttachment(Guid FileId, string Name, long Size, string Url);
public sealed record PublicationMediaSnapshot(Guid? CoverFileId, IReadOnlyList<PublicationInlineImage> InlineImages,
    IReadOnlyList<PublicationAttachment> Attachments);

public static class PublicationMediaUrls
{
    public static string Display(Guid publicationId, Guid fileId) => $"/api/v1/publications/{publicationId:D}/media/{fileId:D}/content?variant=display";
    public static string Attachment(Guid publicationId, Guid fileId) => $"/api/v1/publications/{publicationId:D}/media/{fileId:D}/content?variant=attachment";
    public static bool IsManaged(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 8 && parts[0] == "" && parts[1] == "api" && parts[2] == "v1" && parts[3] == "publications" &&
            Guid.TryParseExact(parts[4], "D", out _) && parts[5] == "media" && Guid.TryParseExact(parts[6], "D", out _) && parts[7] == "content?variant=display";
    }
}

// Participates in the caller's Publishing transaction after the publication lock. Never commits independently.
public interface IPublicationMediaUsages
{
    Task<string?> ReplaceAsync(Guid publicationId, Guid ownerId, string? requestedCoverUrl, PublicationMediaChange change, CancellationToken ct);
    Task RemoveAsync(Guid publicationId, Guid ownerId, CancellationToken ct);
}
