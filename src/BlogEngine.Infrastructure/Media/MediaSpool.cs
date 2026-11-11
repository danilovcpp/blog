using System.Buffers;
using System.Security.Cryptography;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Media;

public sealed class MediaSpool(IOptions<MediaOptions> options, ILogger<MediaSpool> logger) : IMediaSpool, IAsyncDisposable
{
    private readonly MediaOptions settings = options.Value;
    private readonly ILogger<MediaSpool> log = logger;
    private readonly SemaphoreSlim transfers = new(options.Value.Transfers.Concurrency);
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly HashSet<SpoolFile> files = [];
    private FileStream? ownership;
    private long used;
    private Task? stopTask;
    private int disposed;
    public bool IsReady { get; private set; }
    public long UsedBytes => Interlocked.Read(ref used);

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var directory = Directory.CreateDirectory(settings.Spool.Directory);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { throw new IOException("Spool must be a private physical directory."); }
            ownership = new FileStream(Path.Combine(directory.FullName, ".media-owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (var path in Directory.EnumerateFiles(directory.FullName, "media-*.spool", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new IOException("Unexpected spool link."); }
                File.Delete(path);
            }
            var probe = Path.Combine(directory.FullName, "media-" + Guid.NewGuid().ToString("N") + ".spool");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            if (new DriveInfo(directory.FullName).AvailableFreeSpace < settings.Spool.BudgetBytes)
            { throw new IOException("Insufficient spool volume capacity."); }
            IsReady = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ownership?.Dispose(); ownership = null;
            log.LogError("Media spool preparation failed: {ExceptionType}", error.GetType().Name);
        }
        return Task.CompletedTask;
    }

    public async Task<IMediaTransfer> AcquireTransferAsync(CancellationToken ct)
    {
        RequireReady();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        linked.CancelAfter(settings.Transfers.TotalTimeout);
        var acquired = false;
        try { await transfers.WaitAsync(linked.Token); acquired = true; RequireReady(); MediaTelemetry.ActiveTransfers.Add(1); return new Transfer(transfers, linked); }
        catch { if (acquired) { transfers.Release(); } linked.Dispose(); throw; }
    }

    public async Task<bool> CheckReadinessAsync(CancellationToken ct)
    {
        if (!IsReady || stopping.IsCancellationRequested || UsedBytes >= settings.Spool.BudgetBytes) { return false; }
        var probe = Path.Combine(settings.Spool.Directory, "media-" + Guid.NewGuid().ToString("N") + ".spool");
        try
        {
            if (new DriveInfo(settings.Spool.Directory).AvailableFreeSpace < settings.Spool.BudgetBytes - UsedBytes) { return false; }
            await using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
            { await file.WriteAsync(new byte[1], ct); await file.FlushAsync(ct); }
            MediaTelemetry.SpoolFree.Record(settings.Spool.BudgetBytes - UsedBytes);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public Task<IMediaSpoolFile> CaptureAsync(Stream source, long maximumBytes, CancellationToken ct) =>
        CreateAsync(maximumBytes, async (target, token) => await source.CopyToAsync(target, 64 * 1024, token), ct);

    public async Task<IMediaSpoolFile> CreateAsync(long maximumBytes, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        RequireReady();
        if (maximumBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(maximumBytes)); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        linked.CancelAfter(settings.Transfers.TotalTimeout);
        var path = Path.Combine(settings.Spool.Directory, "media-" + Guid.NewGuid().ToString("N") + ".spool");
        var file = new SpoolFile(this, path);
        lock (gate) { RequireReady(); files.Add(file); }
        try
        {
            await using (var target = OpenWritable(path))
            await using (var bounded = new BudgetStream(this, target, file, maximumBytes, linked.Token))
            { await write(bounded, linked.Token); await bounded.FlushAsync(linked.Token); }
            // A second streaming pass also hashes native codec writes without buffering the encoded PNG.
            await using var input = file.OpenRead();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), linked.Token)) != 0)
                { sha.AppendData(buffer.AsSpan(0, count)); md5.AppendData(buffer.AsSpan(0, count)); }
                file.SetHashes(sha.GetHashAndReset(), md5.GetHashAndReset());
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            linked.Token.ThrowIfCancellationRequested();
            MediaTelemetry.TransferSize.Record(file.Length);
            return file;
        }
        catch { await file.DisposeAsync(); throw; }
        finally { file.FinishWriting(); }
    }

    private void RequireReady()
    { if (!IsReady || stopping.IsCancellationRequested) { throw new DomainError("media_capacity_unavailable"); } }

    private static FileStream OpenWritable(string path)
    {
        try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new DomainError("media_capacity_unavailable"); }
    }

    private void Reserve(SpoolFile file, int count, long maximum)
    {
        lock (gate)
        {
            RequireReady();
            if (file.Length > maximum - count) { throw new DomainError("request_too_large"); }
            if (used > settings.Spool.BudgetBytes - count) { throw new DomainError("media_capacity_unavailable"); }
            used += count; file.AddLength(count); MediaTelemetry.SpoolBytes.Add(count);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (gate) { return stopTask ??= StopCoreAsync(); }
    }
    private async Task StopCoreAsync()
    {
        IsReady = false; await stopping.CancelAsync();
        SpoolFile[] active; lock (gate) { active = files.ToArray(); }
        foreach (var file in active) { await file.WritingFinished; await file.DisposeAsync(); }
        ownership?.Dispose(); ownership = null;
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        if (Interlocked.Exchange(ref disposed, 1) == 0) { stopping.Dispose(); transfers.Dispose(); }
    }

    private sealed class Transfer(SemaphoreSlim slots, CancellationTokenSource source) : IMediaTransfer
    {
        private int disposed;
        public CancellationToken CancellationToken => source.Token;
        public ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref disposed, 1) == 0) { MediaTelemetry.ActiveTransfers.Add(-1); slots.Release(); source.Dispose(); } return ValueTask.CompletedTask; }
    }

    private sealed class SpoolFile(MediaSpool owner, string path) : IMediaSpoolFile
    {
        private int disposed;
        private readonly TaskCompletionSource writingFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WritingFinished => writingFinished.Task;
        public void FinishWriting() => writingFinished.TrySetResult();
        public long Length { get; private set; }
        public ReadOnlyMemory<byte> Sha256 { get; private set; }
        public ReadOnlyMemory<byte> Md5 { get; private set; }
        public void AddLength(int count) => Length += count;
        public void SetHashes(byte[] sha, byte[] md5) { Sha256 = sha; Md5 = md5; }
        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) { return ValueTask.CompletedTask; }
            lock (owner.gate)
            {
                try { File.Delete(path); owner.used -= Length; MediaTelemetry.SpoolBytes.Add(-Length); owner.files.Remove(this); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { owner.IsReady = false; owner.log.LogError("Media spool cleanup failed: {ExceptionType}", e.GetType().Name); }
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BudgetStream(MediaSpool owner, Stream target, SpoolFile file, long maximum, CancellationToken ct) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested(); owner.Reserve(file, count, maximum);
            try { target.Write(buffer, offset, count); }
            catch (IOException) { throw new DomainError("media_capacity_unavailable"); }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ct.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested(); owner.Reserve(file, buffer.Length, maximum);
            try { await target.WriteAsync(buffer, cancellationToken); }
            catch (IOException) { throw new DomainError("media_capacity_unavailable"); }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush()
        { try { target.Flush(); } catch (IOException) { throw new DomainError("media_capacity_unavailable"); } }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        { try { await target.FlushAsync(cancellationToken); } catch (IOException) { throw new DomainError("media_capacity_unavailable"); } }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
