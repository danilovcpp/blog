using System.Text.Json;
using BlogEngine.Core.Media.Domain;

namespace BlogEngine.Core.Media.Contracts;

public sealed record UploadResult(Guid UploadId, Guid FileId, MediaPurpose Purpose, DateTimeOffset CompletedAt, Guid? AppliedAvatarVersion);

public sealed record UploadSnapshot(Guid Id, Guid FileId, MediaPurpose Purpose, string Name, long DeclaredSize,
    string DeclaredContentType, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, MediaUploadState State, UploadResult? Result, string? ErrorCode)
{
    public static UploadSnapshot From(MediaUpload upload, MediaFile file, DateTimeOffset now) => new(upload.Id, file.Id,
        file.Purpose, file.OriginalName, file.OriginalSize, file.DeclaredContentType, upload.CreatedAt, upload.ExpiresAt,
        upload.EffectiveState(now), upload.Result is null ? null : JsonSerializer.Deserialize<UploadResult>(upload.Result), upload.ErrorCode);
}

public interface IUploadLifecycleStore
{
    Task<UploadSnapshot> InitializeAsync(MediaFile candidate, MediaUpload request, CancellationToken ct);
    Task<UploadSnapshot> GetAsync(Guid ownerId, Guid uploadId, CancellationToken ct);
    Task CancelAsync(Guid ownerId, Guid uploadId, CancellationToken ct);
}

public sealed record UploadTransferClaim(Guid UploadId, Guid OwnerId, Guid FileId, MediaPurpose Purpose, long Generation,
    long ExpectedSize, byte[]? AcceptedHash, Guid? ObjectId, string? ObjectKey, DateTimeOffset LeaseUntil)
{
    public bool IsReplay => AcceptedHash is not null;
}

public interface IUploadTransferStore
{
    Task<UploadTransferClaim> ClaimAsync(Guid ownerId, Guid uploadId, CancellationToken ct);
    Task AcceptAsync(UploadTransferClaim claim, IMediaSpoolFile content, CancellationToken ct);
    Task ReleaseAsync(UploadTransferClaim claim, string? terminalError, CancellationToken ct);
}

public sealed record UploadCompletionAttempt(UploadSnapshot? Upload, string? Error);
public sealed record MediaProcessingClaim(Guid UploadId, Guid OwnerId, Guid FileId, MediaPurpose Purpose, long Generation,
    Guid SourceId, MediaBucketKind Bucket, string Key, long Length, byte[] Sha256, string ContentType);

// Each purpose registers its own preparation and atomic finalization. No image purpose is dispatched until registered.
public interface IMediaUploadProcessor
{
    MediaPurpose Purpose { get; }
    Task VerifyAsync(MediaProcessingClaim claim, CancellationToken ct);
    Task FinalizeAsync(MediaProcessingClaim claim, CancellationToken ct);
}

public interface IMediaProcessingStore
{
    Task<UploadCompletionAttempt> RequestAsync(Guid ownerId, Guid uploadId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> DueAsync(CancellationToken ct);
    Task<MediaProcessingClaim?> ClaimAsync(Guid uploadId, CancellationToken ct);
    Task RenewAsync(MediaProcessingClaim claim, CancellationToken ct);
    Task CompleteAttachmentAsync(MediaProcessingClaim claim, CancellationToken ct);
    Task RetryAsync(MediaProcessingClaim claim, CancellationToken ct);
}

public interface IMediaProcessingLease
{
    Task RunAsync(MediaProcessingClaim claim, Func<CancellationToken, Task> process, CancellationToken ct);
}
