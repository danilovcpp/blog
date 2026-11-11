using System.Security.Cryptography;
using System.Text.Json;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Domain;

public enum MediaUploadState : short { AwaitingContent, ContentReceived, Processing, Completed, Rejected, Cancelled, Expired }

public sealed class MediaUpload
{
    private MediaUpload() { }
    public Guid Id { get; private set; }
    public Guid FileId { get; private set; }
    public Guid OwnerId { get; private set; }
    public MediaPurpose Purpose { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public byte[] RequestFingerprint { get; private set; } = [];
    public MediaUploadState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public long ReservedBytes { get; private set; }
    public Guid? CurrentObjectId { get; private set; }
    public byte[]? ContentSha256 { get; private set; }
    public long? ActualSize { get; private set; }
    public Guid? ExpectedAvatarVersion { get; private set; }
    public long LeaseGeneration { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public int Attempts { get; private set; }
    public string? Result { get; private set; }
    public string? ErrorCode { get; private set; }
    public bool IsActive => State is MediaUploadState.AwaitingContent or MediaUploadState.ContentReceived or MediaUploadState.Processing;

    public static MediaUpload Create(MediaFile file, Guid key, Guid? expectedAvatarVersion, DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (file.State != MediaFileState.Pending || key == Guid.Empty ||
            (file.Purpose == MediaPurpose.Avatar) != expectedAvatarVersion.HasValue)
        { throw DomainError.Validation("expectedAvatarVersion"); }
        return new()
        {
            Id = Guid.NewGuid(),
            FileId = file.Id,
            OwnerId = file.OwnerId,
            Purpose = file.Purpose,
            IdempotencyKey = key,
            RequestFingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { file.Purpose, file.OriginalName, file.DeclaredContentType, file.OriginalSize, ExpectedAvatarVersion = expectedAvatarVersion })),
            ExpectedAvatarVersion = expectedAvatarVersion,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
            ReservedBytes = file.OriginalSize
        };
    }

    public MediaUploadState EffectiveState(DateTimeOffset now) => IsActive && now >= ExpiresAt ? MediaUploadState.Expired : State;

    public long Claim(DateTimeOffset now, TimeSpan duration)
    {
        RequireActive(now);
        if (duration <= TimeSpan.Zero || LeaseUntil > now) { throw new DomainError("upload_in_progress"); }
        LeaseGeneration = checked(LeaseGeneration + 1); LeaseUntil = now + duration; Attempts = checked(Attempts + 1);
        return LeaseGeneration;
    }

    public void AcceptContent(long generation, Guid objectId, long length, byte[] sha256, DateTimeOffset now)
    {
        RequireLease(generation, now);
        if (State != MediaUploadState.AwaitingContent || length != ReservedBytes || objectId == Guid.Empty) { throw new DomainError("invalid_state"); }
        CurrentObjectId = objectId; ActualSize = length; ContentSha256 = MediaRules.Hash(sha256);
        State = MediaUploadState.ContentReceived; LeaseUntil = null;
    }

    public void StartProcessing(DateTimeOffset now)
    {
        RequireActive(now);
        if (State != MediaUploadState.ContentReceived) { throw new DomainError("invalid_state"); }
        State = MediaUploadState.Processing; NextAttemptAt = now;
    }

    public void Complete(long generation, string snapshot, DateTimeOffset now)
    {
        RequireLease(generation, now);
        if (State != MediaUploadState.Processing || string.IsNullOrWhiteSpace(snapshot)) { throw new DomainError("invalid_state"); }
        State = MediaUploadState.Completed; Result = snapshot; LeaseUntil = null; NextAttemptAt = null;
    }

    public void Terminate(MediaUploadState state, string code, DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (!IsActive || state is not (MediaUploadState.Rejected or MediaUploadState.Cancelled or MediaUploadState.Expired) ||
            string.IsNullOrWhiteSpace(code)) { throw new DomainError("invalid_state"); }
        State = state; ErrorCode = code; LeaseUntil = null; NextAttemptAt = null; LeaseGeneration = checked(LeaseGeneration + 1);
    }

    public void Retry(long generation, DateTimeOffset now, TimeSpan delay)
    {
        RequireLease(generation, now);
        if (delay <= TimeSpan.Zero || delay > TimeSpan.FromMinutes(15)) { throw new ArgumentOutOfRangeException(nameof(delay)); }
        LeaseUntil = null; NextAttemptAt = now + delay;
    }

    private void RequireActive(DateTimeOffset now)
    {
        MediaRules.Utc(now);
        if (!IsActive || now >= ExpiresAt) { throw new DomainError("upload_expired"); }
    }

    private void RequireLease(long generation, DateTimeOffset now)
    {
        RequireActive(now);
        if (generation != LeaseGeneration || LeaseUntil is null || LeaseUntil <= now) { throw new DomainError("concurrency_conflict"); }
    }
}
