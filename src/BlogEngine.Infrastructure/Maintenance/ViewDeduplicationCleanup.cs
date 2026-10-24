using System.Data;
using BlogEngine.Core.Engagement.Domain;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BlogEngine.Infrastructure.Maintenance;

public sealed class ViewDeduplicationCleanup(BlogDbContext db, DatabaseClock clock)
{
    public const int BatchSize = 500;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public async Task<int> RunBatchAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10)); var token = timeout.Token;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var cutoff = await clock.ReadAsync(db, token) - ViewDeduplicationRecord.CountingWindow;
        // No dedup locks here: writers and all cleanup replicas lock publications first.
        var candidates = await db.ViewDeduplicationRecords.AsNoTracking().Where(v => v.LastCountedAt <= cutoff)
            .OrderBy(v => v.LastCountedAt).ThenBy(v => v.PublicationId).ThenBy(v => v.VisitorKind).ThenBy(v => v.VisitorKey)
            .Take(BatchSize).ToListAsync(token);
        if (candidates.Count == 0) { return 0; }
        var publicationIds = candidates.Select(v => v.PublicationId).Distinct().ToArray();
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM publishing.publications WHERE id=ANY({publicationIds}) ORDER BY id FOR UPDATE").ToListAsync(token);
        var now = await clock.ReadAsync(db, token);
        var removed = await db.Database.SqlQueryRaw<int>("""
            DELETE FROM engagement.view_deduplication_records AS d
            USING unnest(@ids,@kinds,@keys) AS c(publication_id,visitor_kind,visitor_key)
            WHERE d.publication_id=c.publication_id AND d.visitor_kind=c.visitor_kind AND d.visitor_key=c.visitor_key
                AND d.last_counted_at <= @now - @window
            RETURNING 1 AS "Value"
            """,
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = candidates.Select(v => v.PublicationId).ToArray() },
            new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = candidates.Select(v => (short)v.VisitorKind).ToArray() },
            new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = candidates.Select(v => v.VisitorKey).ToArray() },
            new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
            new NpgsqlParameter("window", NpgsqlDbType.Interval) { Value = ViewDeduplicationRecord.CountingWindow }).ToListAsync(token);
        await transaction.CommitAsync(token);
        return removed.Count;
    }
}
