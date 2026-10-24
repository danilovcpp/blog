using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Identity;

public sealed class DependencyReadiness(BlogDbContext database, JwksLoader keys)
{
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            // CanConnect swallows provider errors; a direct bounded SELECT keeps cancellation and failures explicit.
            await database.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
        }
        catch (Exception exception) when (DatabaseErrorMapper.Map(exception) is not null
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new DomainError("dependency_unavailable");
        }
        await keys.GetAsync(null, cancellationToken);
    }
}
