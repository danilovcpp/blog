using BlogEngine.Infrastructure.Maintenance;

namespace BlogEngine.Api.Configuration;

public sealed class ViewCleanupWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<ViewCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ViewDeduplicationCleanup.Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var removed = await scope.ServiceProvider.GetRequiredService<ViewDeduplicationCleanup>().RunBatchAsync(stoppingToken);
                    logger.LogInformation("View deduplication cleanup removed {RemovedRows} expired records", removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    // Exception messages may contain SQL or parameters. Failure only retains expired rows.
                    logger.LogWarning("View deduplication cleanup failed ({ErrorType})", error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
