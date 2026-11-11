using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Media;

public sealed class MediaWriteStore(BlogDbContext db, DatabaseClock clock, IAccountRoleWriteGuard roles) : IMediaWriteStore
{
    public async Task<T> ExecuteAsync<T>(MediaWriteLocks locks, Func<IMediaWriteSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (locks.RoleActorId is { } actor) { AccessPolicy.Demand(await roles.LockAndReadAsync(actor, ct), ActorAction.CreatePublication); }
        if (locks.PublicationId is { } publication)
        {
            var owner = await db.Database.SqlQuery<Guid>($"SELECT author_id AS \"Value\" FROM publishing.publications WHERE id={publication} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (owner != locks.OwnerId) { throw new DomainError("resource_not_found"); }
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO media.allowances(owner_id,limit_bytes,used_bytes) VALUES ({locks.OwnerId},{MediaRules.DefaultAllowance},0) ON CONFLICT DO NOTHING", ct);
        var allowance = await db.StorageAllowances.FromSqlInterpolated($"SELECT * FROM media.allowances WHERE owner_id={locks.OwnerId} FOR UPDATE").SingleAsync(ct);
        await db.Entry(allowance).ReloadAsync(ct);
        if (locks.Avatar)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO media.avatar_states(user_id,version,updated_at) VALUES ({locks.OwnerId},{Guid.NewGuid()},clock_timestamp()) ON CONFLICT DO NOTHING", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM media.avatar_states WHERE user_id={locks.OwnerId} FOR UPDATE", ct);
        }
        foreach (var id in (locks.FileIds ?? []).Distinct().Order())
        { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM media.files WHERE id={id} AND owner_id={locks.OwnerId} FOR UPDATE", ct); }
        var now = await clock.ReadAsync(db, ct);
        var result = await operation(new Session(db, clock, locks.OwnerId, allowance, now), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private sealed class Session(BlogDbContext db, DatabaseClock clock, Guid ownerId, StorageAllowance allowance, DateTimeOffset now) : IMediaWriteSession
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public async Task<MediaQuota> QuotaAsync(CancellationToken ct)
        {
            var reserved = await db.MediaUploads.Where(u => u.OwnerId == ownerId &&
                (u.State == MediaUploadState.AwaitingContent || u.State == MediaUploadState.ContentReceived || u.State == MediaUploadState.Processing) && u.ExpiresAt > UtcNow)
                .SumAsync(u => u.ReservedBytes, ct);
            return new(allowance.LimitBytes, allowance.UsedBytes, reserved);
        }
        public async Task ReserveAsync(MediaFile file, MediaUpload upload, CancellationToken ct)
        {
            if (file.OwnerId != ownerId || upload.OwnerId != ownerId || upload.FileId != file.Id || upload.ReservedBytes != file.OriginalSize ||
                file.State != MediaFileState.Pending || upload.State != MediaUploadState.AwaitingContent)
            { throw new DomainError("invalid_state"); }
            if ((await QuotaAsync(ct)).Available < upload.ReservedBytes) { throw new DomainError("quota_exceeded"); }
            db.MediaFiles.Add(file); await db.SaveChangesAsync(ct);
            db.MediaUploads.Add(upload); await db.SaveChangesAsync(ct);
        }
        public async Task ApplyUsageAsync(long delta, CancellationToken ct)
        {
            if (delta > 0 && (await QuotaAsync(ct)).Available < delta) { throw new DomainError("quota_exceeded"); }
            allowance.ApplyUsage(delta); await db.SaveChangesAsync(ct);
        }
        public async Task<MediaUpload?> LockUploadAsync(Guid uploadId, CancellationToken ct)
        {
            var upload = await db.MediaUploads.FromSqlInterpolated($"SELECT * FROM media.uploads WHERE id={uploadId} AND owner_id={ownerId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (upload is not null) { await db.Entry(upload).ReloadAsync(ct); }
            UtcNow = await clock.ReadAsync(db, ct);
            return upload;
        }
        public async Task<long> ClaimUploadAsync(Guid uploadId, TimeSpan duration, CancellationToken ct)
        {
            var upload = await LockUploadAsync(uploadId, ct) ?? throw new DomainError("resource_not_found");
            var generation = upload.Claim(UtcNow, duration); await db.SaveChangesAsync(ct); return generation;
        }
        public async Task<MediaUpload> RequireFenceAsync(Guid uploadId, long generation, CancellationToken ct)
        {
            var upload = await LockUploadAsync(uploadId, ct) ?? throw new DomainError("resource_not_found");
            if (!upload.IsActive || upload.ExpiresAt <= UtcNow || upload.LeaseUntil is null || upload.LeaseUntil <= UtcNow || upload.LeaseGeneration != generation)
            { throw new DomainError("concurrency_conflict"); }
            return upload;
        }
        public async Task SaveObjectPlanAsync(MediaObject plan, CancellationToken ct)
        {
            var upload = await RequireFenceAsync(plan.UploadId, plan.AttemptGeneration, ct);
            if (upload.FileId != plan.FileId || upload.State is not (MediaUploadState.AwaitingContent or MediaUploadState.Processing))
            { throw new DomainError("concurrency_conflict"); }
            db.MediaObjects.Add(plan); await db.SaveChangesAsync(ct);
        }
        public async Task RetireAsync(Guid objectId, DateTimeOffset notBefore, CancellationToken ct)
        {
            var obj = await db.MediaObjects.FromSqlInterpolated($"SELECT o.* FROM media.objects o JOIN media.files f ON f.id=o.file_id WHERE o.id={objectId} AND f.owner_id={ownerId} FOR UPDATE OF o").SingleOrDefaultAsync(ct)
                ?? throw new DomainError("resource_not_found");
            await db.Entry(obj).ReloadAsync(ct);
            obj.Retire(UtcNow);
            if (!await db.MediaCleanupJobs.AnyAsync(j => j.ObjectId == obj.Id, ct))
            { db.MediaCleanupJobs.Add(MediaCleanupJob.Create(obj.Id, notBefore > UtcNow ? notBefore : UtcNow)); }
            await db.SaveChangesAsync(ct);
        }
    }
}
