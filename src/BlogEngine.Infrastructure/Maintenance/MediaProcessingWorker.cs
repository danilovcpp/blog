using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Infrastructure.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Maintenance;

public sealed class MediaProcessingWorker(IServiceScopeFactory scopes, IOptions<MediaOptions> options, ILogger<MediaProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (options.Value.MaintenanceMode) { continue; }
                try
                {
                    IReadOnlyList<Guid> due;
                    await using (var scope = scopes.CreateAsyncScope())
                    { due = await scope.ServiceProvider.GetRequiredService<IMediaProcessingStore>().DueAsync(stoppingToken); }
                    await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = options.Value.Processing.Concurrency, CancellationToken = stoppingToken },
                        async (id, token) =>
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, token);
                        });
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    // Do not log exception messages: drivers can embed addresses, SQL or credentials.
                    logger.LogWarning("Media processing cycle failed with {ExceptionType}; durable claims remain retryable", error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
