using BlogEngine.Core.Media.Domain;

namespace BlogEngine.Core.Media.Contracts;

public sealed record MediaWriteLocks(Guid OwnerId, Guid? RoleActorId = null, Guid? PublicationId = null,
    bool Avatar = false, IReadOnlyList<Guid>? FileIds = null);
public sealed record MediaQuota(long Limit, long Used, long Reserved)
{
    public long Available => checked(Limit - Used - Reserved);
}

public interface IMediaWriteStore
{
    Task<T> ExecuteAsync<T>(MediaWriteLocks locks, Func<IMediaWriteSession, CancellationToken, Task<T>> operation, CancellationToken ct);
}

// Short database operations only. Callers leave the scope before transfers or image processing.
public interface IMediaWriteSession
{
    DateTimeOffset UtcNow { get; }
    Task<MediaQuota> QuotaAsync(CancellationToken ct);
    Task ReserveAsync(MediaFile file, MediaUpload upload, CancellationToken ct);
    Task ApplyUsageAsync(long delta, CancellationToken ct);
    Task<MediaUpload?> LockUploadAsync(Guid uploadId, CancellationToken ct);
    Task<long> ClaimUploadAsync(Guid uploadId, TimeSpan duration, CancellationToken ct);
    Task<MediaUpload> RequireFenceAsync(Guid uploadId, long generation, CancellationToken ct);
    Task SaveObjectPlanAsync(MediaObject plan, CancellationToken ct);
    Task RetireAsync(Guid objectId, DateTimeOffset notBefore, CancellationToken ct);
}
