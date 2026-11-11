using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Domain;

public sealed class AvatarState
{
    private AvatarState() { }
    public Guid UserId { get; private set; }
    public Guid? CurrentFileId { get; private set; }
    public Guid Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public static AvatarState Empty(Guid userId, DateTimeOffset now)
    {
        MediaRules.Utc(now);
        return new() { UserId = userId, Version = Guid.NewGuid(), UpdatedAt = now };
    }
    public void Replace(Guid expectedVersion, MediaFile? file, DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (expectedVersion != Version) { throw new DomainError("concurrency_conflict"); }
        if (file is not null && (file.OwnerId != UserId || file.Purpose != MediaPurpose.Avatar || file.State != MediaFileState.Ready))
        { throw new DomainError("invalid_state"); }
        if (CurrentFileId == file?.Id) { return; }
        CurrentFileId = file?.Id; Version = Guid.NewGuid(); UpdatedAt = now;
    }
}
