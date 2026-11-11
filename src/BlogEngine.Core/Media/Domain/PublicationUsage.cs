using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Domain;

public enum MediaUsageRole : short { Cover, Inline, Attachment }

public sealed class PublicationUsage
{
    private PublicationUsage() { }
    public Guid PublicationId { get; private set; }
    public Guid FileId { get; private set; }
    public Guid PublicationOwnerId { get; private set; }
    public MediaUsageRole Role { get; private set; }
    public int Position { get; private set; }

    public static void ValidateIds(MediaUsageRole role, IReadOnlyList<Guid> ids)
    {
        var max = role switch { MediaUsageRole.Cover => 1, MediaUsageRole.Inline => 50, MediaUsageRole.Attachment => 20, _ => throw DomainError.Validation("role") };
        if (ids.Count > max || ids.Distinct().Count() != ids.Count || ids.Contains(Guid.Empty)) { throw DomainError.Validation("files"); }
    }

    public static IReadOnlyList<PublicationUsage> ForRole(Guid publicationId, Guid ownerId, MediaUsageRole role,
        IReadOnlyList<MediaFile> files, bool hasExternalCover)
    {
        ValidateIds(role, files.Select(f => f.Id).ToArray());
        var purpose = role == MediaUsageRole.Attachment ? MediaPurpose.Attachment : MediaPurpose.PublicationImage;
        if ((role == MediaUsageRole.Cover && hasExternalCover && files.Count != 0) ||
            files.Any(f => f.OwnerId != ownerId || f.State != MediaFileState.Ready || f.Purpose != purpose))
        { throw DomainError.Validation("files"); }
        return files.Select((f, i) => new PublicationUsage
        {
            PublicationId = publicationId,
            PublicationOwnerId = ownerId,
            FileId = f.Id,
            Role = role,
            Position = i
        }).ToArray();
    }
}
