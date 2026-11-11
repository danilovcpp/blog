using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Domain;

public enum MediaBucketKind : short { Originals, Renditions }
public enum MediaObjectKind : short { Source, Display, Avatar256, Avatar512 }
public enum MediaObjectState : short { Planned, Present, Retired, Deleted }

public sealed class MediaObject
{
    private MediaObject() { }
    public Guid Id { get; private set; }
    public Guid UploadId { get; private set; }
    public Guid FileId { get; private set; }
    public MediaBucketKind BucketKind { get; private set; }
    public string ObjectKey { get; private set; } = "";
    public long AttemptGeneration { get; private set; }
    public MediaObjectKind Kind { get; private set; }
    public MediaObjectState State { get; private set; }
    public long? VerifiedLength { get; private set; }
    public byte[]? Sha256 { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }
    public long Generation { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset NextReconcileAt { get; private set; }
    public DateTimeOffset? LastReconciledAt { get; private set; }

    public static MediaObject Plan(MediaUpload upload, MediaObjectKind kind, DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (!Enum.IsDefined(kind) || !upload.IsActive || now >= upload.ExpiresAt) { throw new DomainError("invalid_state"); }
        var id = Guid.NewGuid();
        return new()
        {
            Id = id,
            UploadId = upload.Id,
            FileId = upload.FileId,
            Kind = kind,
            BucketKind = kind == MediaObjectKind.Source ? MediaBucketKind.Originals : MediaBucketKind.Renditions,
            ObjectKey = id.ToString("N"),
            AttemptGeneration = upload.LeaseGeneration,
            CreatedAt = now,
            NextReconcileAt = now
        };
    }
    public void MarkPresent(long length, byte[] sha256)
    {
        if (State != MediaObjectState.Planned || length <= 0) { throw new DomainError("invalid_state"); }
        VerifiedLength = length; Sha256 = MediaRules.Hash(sha256); State = MediaObjectState.Present;
    }
    public void Retire(DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (State is MediaObjectState.Retired or MediaObjectState.Deleted) { return; }
        State = MediaObjectState.Retired; RetiredAt = now; NextReconcileAt = now;
    }

    public long ClaimCleanup(DateTimeOffset now, TimeSpan duration)
    {
        MediaRules.Utc(now);
        if (State is not (MediaObjectState.Retired or MediaObjectState.Deleted) || LeaseUntil > now || duration <= TimeSpan.Zero)
        { throw new DomainError("concurrency_conflict"); }
        Generation = checked(Generation + 1); LeaseUntil = now + duration; return Generation;
    }

    public bool OwnsCleanup(long generation, DateTimeOffset now) => Generation == generation && LeaseUntil > now &&
        State is MediaObjectState.Retired or MediaObjectState.Deleted;

    public void FinishCleanup(long generation, DateTimeOffset now, DateTimeOffset next, bool deleted)
    {
        if (!OwnsCleanup(generation, now)) { throw new DomainError("concurrency_conflict"); }
        if (deleted) { State = MediaObjectState.Deleted; LastReconciledAt = now; }
        LeaseUntil = null; NextReconcileAt = next;
    }
}
