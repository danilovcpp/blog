using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class MediaExpiryStore(BlogDbContext db, DatabaseClock clock, IMediaWriteStore writes, IOptions<MediaOptions> options)
{
    public async Task<int> RunBatchAsync(CancellationToken ct)
    {
        if (options.Value.MaintenanceMode) { return 0; }
        var now = await clock.ReadAsync(db, ct);
        var due = await db.MediaUploads.AsNoTracking().Where(u =>
                (u.State == MediaUploadState.AwaitingContent || u.State == MediaUploadState.ContentReceived || u.State == MediaUploadState.Processing) && u.ExpiresAt <= now)
            .OrderBy(u => u.ExpiresAt).ThenBy(u => u.Id).Select(u => new { u.Id, u.OwnerId, u.FileId })
            .Take(options.Value.Cleanup.BatchSize).ToArrayAsync(ct);
        var count = 0;
        foreach (var candidate in due)
        {
            count += await writes.ExecuteAsync(new(candidate.OwnerId, FileIds: [candidate.FileId]), async (session, token) =>
            {
                if (options.Value.MaintenanceMode) { return 0; }
                var upload = await session.LockUploadAsync(candidate.Id, token);
                if (upload is null || !upload.IsActive || upload.ExpiresAt > session.UtcNow) { return 0; }
                await MediaUploadTransitions.TerminateAsync(db, session, upload, MediaUploadState.Expired, "upload_expired", token);
                return 1;
            }, ct);
        }
        return count;
    }
}
