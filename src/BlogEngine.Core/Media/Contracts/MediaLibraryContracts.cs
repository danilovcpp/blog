using BlogEngine.Core.Media.Domain;

namespace BlogEngine.Core.Media.Contracts;

public sealed record LibraryFile(Guid Id, Guid OwnerId, MediaPurpose Purpose, string Name, long Size, string ContentType, DateTimeOffset CreatedAt);
public sealed record LibraryPage(int Page, int PageSize, IReadOnlyList<LibraryFile> Items, bool HasNextPage);
public sealed record LibraryContent(MediaBucketKind Bucket, string Key, long Length, byte[] Sha256, string Name);

public interface IMediaLibraryQuery
{
    Task<LibraryPage> ListAsync(Guid actorId, Guid? ownerId, int page, int pageSize, CancellationToken ct);
    Task<LibraryFile?> GetAsync(Guid actorId, Guid fileId, CancellationToken ct);
    Task<LibraryContent?> ContentAsync(Guid actorId, Guid fileId, CancellationToken ct);
}
