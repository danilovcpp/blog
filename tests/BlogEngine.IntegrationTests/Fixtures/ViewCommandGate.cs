using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlogEngine.IntegrationTests.Fixtures;

internal sealed class ViewCommandGate(Func<string, bool> matches) : DbCommandInterceptor
{
    private int armed;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Arm() => Interlocked.Exchange(ref armed, 1);
    private async Task WaitAsync(DbCommand command, CancellationToken ct)
    {
        if (matches(command.CommandText) && Interlocked.Exchange(ref armed, 0) == 1)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
    }
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { await WaitAsync(command, cancellationToken); return result; }
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { await WaitAsync(command, cancellationToken); return result; }
}
