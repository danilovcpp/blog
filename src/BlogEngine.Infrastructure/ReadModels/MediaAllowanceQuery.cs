using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class MediaAllowanceQuery(BlogDbContext db, DatabaseClock clock)
{
    public async Task<MediaQuota> ReadAsync(Guid actorId, Guid? ownerId, CancellationToken ct)
    {
        // Keep the clock read and counters on one snapshot so a new reservation cannot be counted alongside an expired one it replaced.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var now = await clock.ReadAsync(db, ct);
        var owner = ownerId ?? actorId;
        // Role, counter and reservations are projected by the same SQL snapshot, without bootstrap writes.
        var row = await db.Database.SqlQuery<AllowanceRow>($"""
            SELECT COALESCE((SELECT limit_bytes FROM media.allowances WHERE owner_id={owner}),{MediaRules.DefaultAllowance}) AS "Limit",
                   COALESCE((SELECT used_bytes FROM media.allowances WHERE owner_id={owner}),0) AS "Used",
                   COALESCE((SELECT SUM(reserved_bytes)::bigint FROM media.uploads
                             WHERE owner_id={owner} AND state IN (0,1,2) AND expires_at>{now}),0) AS "Reserved",
                   EXISTS(SELECT 1 FROM accounts.role_assignments WHERE user_id={actorId} AND role={(short)LocalRole.Admin}) AS "Admin"
            """).SingleAsync(ct);
        if (ownerId.HasValue && !row.Admin) { throw new DomainError("forbidden"); }
        await tx.CommitAsync(ct);
        MediaTelemetry.QuotaUsed.Record(row.Used); MediaTelemetry.QuotaReserved.Record(row.Reserved);
        return new(row.Limit, row.Used, row.Reserved);
    }

    private sealed record AllowanceRow(long Limit, long Used, long Reserved, bool Admin);
}
