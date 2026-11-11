using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed record MediaCleanupClaim(Guid ObjectId, Guid OwnerId, Guid FileId, MediaBucketKind Bucket, string Key, long Generation, int Attempt);

public sealed class MediaCleanupStore(BlogDbContext db, DatabaseClock clock, IMediaWriteStore writes, IOptions<MediaOptions> options)
{
    public async Task<IReadOnlyList<Guid>> DueAsync(bool reconciliation, CancellationToken ct)
    {
        if (options.Value.MaintenanceMode) { return []; }
        var now = await clock.ReadAsync(db, ct);
        if (reconciliation)
        {
            return await db.MediaObjects.AsNoTracking().Where(o =>
                    (o.State == MediaObjectState.Retired || o.State == MediaObjectState.Deleted) && o.NextReconcileAt <= now && (o.LeaseUntil == null || o.LeaseUntil <= now) &&
                    db.MediaCleanupJobs.Any(j => j.ObjectId == o.Id && j.NotBefore <= now && (j.LeaseUntil == null || j.LeaseUntil <= now)))
                .OrderBy(o => o.NextReconcileAt).ThenBy(o => o.Id).Select(o => o.Id).Take(options.Value.Cleanup.BatchSize).ToArrayAsync(ct);
        }
        return await db.MediaCleanupJobs.AsNoTracking().Where(j => j.CompletedAt == null && j.NextAttemptAt <= now &&
                (j.LeaseUntil == null || j.LeaseUntil <= now))
            .OrderBy(j => j.NextAttemptAt).ThenBy(j => j.Id).Select(j => j.ObjectId).Take(options.Value.Cleanup.BatchSize).ToArrayAsync(ct);
    }

    public async Task<MediaCleanupClaim?> ClaimAsync(Guid objectId, bool reconciliation, CancellationToken ct)
    {
        var known = await (from o in db.MediaObjects.AsNoTracking()
                           join f in db.MediaFiles.AsNoTracking() on o.FileId equals f.Id
                           where o.Id == objectId
                           select new { f.OwnerId, f.Id }).SingleOrDefaultAsync(ct);
        if (known is null || options.Value.MaintenanceMode) { return null; }
        return await writes.ExecuteAsync<MediaCleanupClaim?>(new(known.OwnerId, FileIds: [known.Id]), async (session, token) =>
        {
            var obj = await LockObjectAsync(objectId, token);
            if (obj is null || options.Value.MaintenanceMode || obj.State is not (MediaObjectState.Retired or MediaObjectState.Deleted) || obj.LeaseUntil > session.UtcNow)
            { return null; }
            var job = await LockJobAsync(objectId, token);
            if (job is null || job.NotBefore > session.UtcNow || job.LeaseUntil > session.UtcNow) { return null; }
            if (reconciliation ? obj.NextReconcileAt > session.UtcNow : job.CompletedAt != null || job.NextAttemptAt > session.UtcNow) { return null; }
            if (await HasActiveReferenceAsync(obj, token)) { return null; }
            // The upload lock fences a concurrent transfer; cancellation does not stop an accepted S3 PUT.
            var upload = await session.LockUploadAsync(obj.UploadId, token);
            if (upload?.LeaseGeneration == obj.AttemptGeneration && upload.LeaseUntil > session.UtcNow) { return null; }
            var duration = options.Value.Storage.RequestTimeout + TimeSpan.FromSeconds(30);
            var generation = obj.ClaimCleanup(session.UtcNow, duration); job.Claim(session.UtcNow, duration);
            return new(obj.Id, known.OwnerId, obj.FileId, obj.BucketKind, obj.ObjectKey, generation, job.Attempts);
        }, ct);
    }

    public async Task FinishAsync(MediaCleanupClaim claim, bool deleted, CancellationToken ct) =>
        await writes.ExecuteAsync(new(claim.OwnerId, FileIds: [claim.FileId]), async (session, token) =>
        {
            var obj = await LockObjectAsync(claim.ObjectId, token);
            if (obj is null || !obj.OwnsCleanup(claim.Generation, session.UtcNow) || await HasActiveReferenceAsync(obj, token)) { return 0; }
            var job = await LockJobAsync(obj.Id, token);
            var delay = deleted ? options.Value.Cleanup.Interval : RetryDelay(claim);
            obj.FinishCleanup(claim.Generation, session.UtcNow, session.UtcNow + delay, deleted);
            job?.Finish(session.UtcNow, session.UtcNow + delay, deleted);
            return 0;
        }, ct);

    private TimeSpan RetryDelay(MediaCleanupClaim claim)
    {
        // Stable jitter makes races reproducible and spreads retries between replicas without shared random state.
        var seconds = Math.Min(options.Value.Cleanup.RetryMaxDelay.TotalSeconds,
            5 * Math.Pow(2, Math.Min(claim.Attempt, 8)) + claim.ObjectId.ToByteArray()[0] % 7);
        return TimeSpan.FromSeconds(seconds);
    }
    private async Task<MediaObject?> LockObjectAsync(Guid id, CancellationToken ct)
    {
        var obj = await db.MediaObjects.FromSqlInterpolated($"SELECT * FROM media.objects WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (obj is not null) { await db.Entry(obj).ReloadAsync(ct); }
        return obj;
    }
    private async Task<MediaCleanupJob?> LockJobAsync(Guid id, CancellationToken ct)
    {
        var job = await db.MediaCleanupJobs.FromSqlInterpolated($"SELECT * FROM media.cleanup_jobs WHERE object_id={id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (job is not null) { await db.Entry(job).ReloadAsync(ct); }
        return job;
    }
    private async Task<bool> HasActiveReferenceAsync(MediaObject obj, CancellationToken ct) =>
        await db.MediaFiles.AnyAsync(f => f.Id == obj.FileId && f.OriginalObjectId == obj.Id, ct) ||
        await db.MediaRepresentations.AnyAsync(r => r.ObjectId == obj.Id, ct);
}
