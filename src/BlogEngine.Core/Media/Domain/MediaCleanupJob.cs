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
}
