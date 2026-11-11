using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Maintenance;

public sealed class MediaProcessingLease(IServiceScopeFactory scopes, IOptions<MediaOptions> options) : IMediaProcessingLease
{
    public async Task RunAsync(MediaProcessingClaim claim, Func<CancellationToken, Task> process, CancellationToken ct)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var renewals = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewAsync();
        try { await process(work.Token); }
        finally
        {
            await renewals.CancelAsync();
            await renewal;
        }

        async Task RenewAsync()
        {
            using var timer = new PeriodicTimer(options.Value.Processing.RenewalInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(renewals.Token))
                {
                    // A separate DbContext prevents concurrent operations on the processing session.
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IMediaProcessingStore>().RenewAsync(claim, renewals.Token);
                }
            }
            catch (OperationCanceledException) when (renewals.IsCancellationRequested) { }
            catch (DomainError error) when (error.Code == "concurrency_conflict") { await work.CancelAsync(); }
            catch { await work.CancelAsync(); throw; }
        }
    }
}
