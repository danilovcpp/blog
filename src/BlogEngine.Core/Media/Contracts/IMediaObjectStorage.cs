using BlogEngine.Core.Media.Domain;

namespace BlogEngine.Core.Media.Contracts;

// The caller holds one shared IMediaTransfer across body/spool/storage/response and passes its deadline token.
public interface IMediaObjectStorage
{
    Task PutAsync(MediaBucketKind bucket, string key, IMediaSpoolFile content, CancellationToken ct);
    Task<IMediaSpoolFile> GetVerifiedAsync(MediaBucketKind bucket, string key, long length, ReadOnlyMemory<byte> sha256, CancellationToken ct);
    Task DeleteAsync(MediaBucketKind bucket, string key, CancellationToken ct);
}

public sealed class MediaStorageException(string code, bool transient) : Exception(code)
{
    public string Code { get; } = code;
    public bool IsTransient { get; } = transient;
}
