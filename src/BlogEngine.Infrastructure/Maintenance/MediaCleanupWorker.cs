using System.Diagnostics;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Maintenance;

public sealed class MediaCleanupCycle(IServiceScopeFactory scopes, IMediaObjectStorage storage, IOptions<MediaOptions> options, ILogger<MediaCleanupCycle> logger)
{
    public async Task<(int Jobs, int Expired, int Reconciled)> RunAsync(CancellationToken ct)
    {
        if (options.Value.MaintenanceMode) { return (0, 0, 0); }
        using var activity = new Activity("media.cleanup").Start();
        var jobs = await ObjectsAsync(false, ct);
        int expired;
        await using (var scope = scopes.CreateAsyncScope())
        { expired = await scope.ServiceProvider.GetRequiredService<MediaExpiryStore>().RunBatchAsync(ct); }
        var reconciled = await ObjectsAsync(true, ct);
        await using (var scope = scopes.CreateAsyncScope())
        { await scope.ServiceProvider.GetRequiredService<MediaTelemetry>().SampleCleanupAsync(ct); }
        return (jobs, expired, reconciled);
    }

    private async Task<int> ObjectsAsync(bool reconciliation, CancellationToken ct)
    {
        IReadOnlyList<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
        { ids = await scope.ServiceProvider.GetRequiredService<MediaCleanupStore>().DueAsync(reconciliation, ct); }
        var count = 0;
        foreach (var id in ids)
        {
            if (options.Value.MaintenanceMode) { break; }
            MediaCleanupClaim? claim;
            await using (var scope = scopes.CreateAsyncScope())
            { claim = await scope.ServiceProvider.GetRequiredService<MediaCleanupStore>().ClaimAsync(id, reconciliation, ct); }
            if (claim is null) { continue; }
            var deleted = false;
            try { await storage.DeleteAsync(claim.Bucket, claim.Key, ct); deleted = true; count++; MediaTelemetry.Cleanup.Add(1, new KeyValuePair<string, object?>("queue", reconciliation ? "reconciliation" : "jobs")); }
            catch (MediaStorageException)
            {
                MediaTelemetry.Retries.Add(1, new KeyValuePair<string, object?>("operation", "cleanup"));
                logger.LogWarning("Media cleanup failed for {JobObjectId} attempt {Attempt} code {Code} traceId {TraceId}",
                    claim.ObjectId, claim.Attempt, "media_storage_unavailable", Activity.Current?.TraceId.ToString());
            }
            await using var finish = scopes.CreateAsyncScope();
            await finish.ServiceProvider.GetRequiredService<MediaCleanupStore>().FinishAsync(claim, deleted, ct);
        }
        return count;
    }
}

public sealed class MediaCleanupWorker(MediaCleanupCycle cycle, IOptions<MediaOptions> options, ILogger<MediaCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Cleanup.Interval);
        try
        {
            do
            {
                try { await cycle.RunAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                { logger.LogWarning("Media cleanup cycle failed with {ExceptionType}; durable claims remain retryable", error.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
