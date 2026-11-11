using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Domain;

public sealed class MediaCleanupJob
{
    private MediaCleanupJob() { }
    public Guid Id { get; private set; }
    public Guid ObjectId { get; private set; }
    public DateTimeOffset NotBefore { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public long Generation { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? ErrorCode { get; private set; }
    public static MediaCleanupJob Create(Guid objectId, DateTimeOffset notBefore)
    {
        MediaRules.Utc(notBefore);
        return new() { Id = Guid.NewGuid(), ObjectId = objectId, NotBefore = notBefore, DueAt = notBefore, NextAttemptAt = notBefore };
    }

    public void Claim(DateTimeOffset now, TimeSpan duration)
    {
        MediaRules.Utc(now);
        if (now < NotBefore || LeaseUntil > now || duration <= TimeSpan.Zero) { throw new DomainError("concurrency_conflict"); }
        Generation = checked(Generation + 1); LeaseUntil = now + duration; Attempts = checked(Attempts + 1);
    }

    public void Finish(DateTimeOffset now, DateTimeOffset next, bool deleted)
    {
        LeaseUntil = null;
        if (deleted) { CompletedAt ??= now; ErrorCode = null; }
        else { NextAttemptAt = next > NotBefore ? next : NotBefore; ErrorCode = "media_storage_unavailable"; }
    }
}
