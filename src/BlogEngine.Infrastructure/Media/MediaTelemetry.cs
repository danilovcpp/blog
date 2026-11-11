using System.Diagnostics;
using System.Diagnostics.Metrics;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Media;

public sealed class MediaTelemetry(BlogDbContext db, DatabaseClock clock, IOptions<MediaOptions> options, ILogger<MediaTelemetry> logger)
{
    private static readonly Meter Meter = new("BlogEngine.Media", "1.0");
    public static readonly UpDownCounter<long> ActiveTransfers = Meter.CreateUpDownCounter<long>("media.transfers.active");
    public static readonly UpDownCounter<long> ActiveDecodes = Meter.CreateUpDownCounter<long>("media.decodes.active");
    public static readonly UpDownCounter<long> SpoolBytes = Meter.CreateUpDownCounter<long>("media.spool.used", "By");
    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("media.requests.duration", "ms");
    public static readonly Counter<long> StorageFailures = Meter.CreateCounter<long>("media.storage.failures");
    public static readonly Counter<long> ReadinessFailures = Meter.CreateCounter<long>("media.readiness.failures");
    public static readonly Counter<long> Retries = Meter.CreateCounter<long>("media.retries");
    public static readonly Counter<long> Cleanup = Meter.CreateCounter<long>("media.cleanup.deleted");
    public static readonly Histogram<long> QuotaUsed = Meter.CreateHistogram<long>("media.quota.used", "By");
    public static readonly Histogram<long> QuotaReserved = Meter.CreateHistogram<long>("media.quota.reserved", "By");
    public static readonly Histogram<long> SpoolFree = Meter.CreateHistogram<long>("media.spool.free", "By");
    public static readonly Histogram<long> TransferSize = Meter.CreateHistogram<long>("media.transfer.size", "By");
    public static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>("media.processing.duration", "ms");
    public static readonly Histogram<double> BacklogAge = Meter.CreateHistogram<double>("media.cleanup.oldest", "s");
    public static readonly Histogram<double> ReconciliationLag = Meter.CreateHistogram<double>("media.cleanup.reconciliation_lag", "s");
    public static readonly Histogram<long> BacklogCount = Meter.CreateHistogram<long>("media.cleanup.backlog");

    public async Task SampleCleanupAsync(CancellationToken ct)
    {
        var now = await clock.ReadAsync(db, ct);
        var row = await db.Database.SqlQuery<CleanupProfile>($"""
            SELECT (SELECT count(*) FROM media.objects WHERE state IN (2,3)) AS "Tombstones",
                (SELECT count(*) FROM media.objects WHERE retired_at>{now.AddHours(-24)}) AS "Retirements",
                (SELECT count(*) FROM media.uploads WHERE state IN (0,1,2) AND expires_at<={now}) AS "Expired",
                (SELECT count(*) FROM media.uploads WHERE expires_at>{now.AddHours(-24)} AND expires_at<={now}) AS "Expiries",
                COALESCE((SELECT EXTRACT(EPOCH FROM ({now}-min(due_at)))::double precision FROM media.cleanup_jobs WHERE completed_at IS NULL),0) AS "OldestSeconds",
                COALESCE((SELECT EXTRACT(EPOCH FROM ({now}-min(COALESCE(last_reconciled_at,retired_at))))::double precision FROM media.objects WHERE state IN (2,3)),0) AS "LagSeconds"
            """).SingleAsync(ct);
        BacklogAge.Record(Math.Max(0, row.OldestSeconds)); ReconciliationLag.Record(Math.Max(0, row.LagSeconds));
        BacklogCount.Record(row.Tombstones, new KeyValuePair<string, object?>("queue", "objects"));
        BacklogCount.Record(row.Expired, new KeyValuePair<string, object?>("queue", "expiry"));
        var settings = options.Value.Cleanup;
        if (row.Tombstones > (long)settings.VerifiedTombstones + settings.VerifiedDailyRetirements || row.Retirements > settings.VerifiedDailyRetirements ||
            row.Expired > settings.VerifiedExpiredBacklog || row.Expiries > settings.VerifiedDailyExpiries ||
            row.OldestSeconds > settings.PendingWarningAge.TotalSeconds || row.LagSeconds > settings.ReconciliationWarningAge.TotalSeconds)
        {
            logger.LogWarning("Media cleanup capacity warning: {Tombstones} tombstones, {Retirements} daily retirements, {Expired} expired, {Expiries} daily expiries, {OldestSeconds}s oldest, {LagSeconds}s reconciliation lag; traceId {TraceId}",
                row.Tombstones, row.Retirements, row.Expired, row.Expiries, row.OldestSeconds, row.LagSeconds, Activity.Current?.TraceId.ToString());
        }
    }

    private sealed record CleanupProfile(long Tombstones, long Retirements, long Expired, long Expiries, double OldestSeconds, double LagSeconds);
}
