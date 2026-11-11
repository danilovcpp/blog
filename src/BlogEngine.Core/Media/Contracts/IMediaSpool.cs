namespace BlogEngine.Core.Media.Contracts;

public interface IMediaSpool
{
    bool IsReady { get; }
    long UsedBytes { get; }
    Task<bool> CheckReadinessAsync(CancellationToken ct);
    Task<IMediaTransfer> AcquireTransferAsync(CancellationToken ct);
    Task<IMediaSpoolFile> CaptureAsync(Stream source, long maximumBytes, CancellationToken ct);
    Task<IMediaSpoolFile> CreateAsync(long maximumBytes, Func<Stream, CancellationToken, Task> write, CancellationToken ct);
}

public interface IMediaTransfer : IAsyncDisposable
{
    CancellationToken CancellationToken { get; }
}

public interface IMediaSpoolFile : IAsyncDisposable
{
    long Length { get; }
    ReadOnlyMemory<byte> Sha256 { get; }
    ReadOnlyMemory<byte> Md5 { get; }
    Stream OpenRead();
}
