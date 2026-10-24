namespace BlogEngine.IntegrationTests.Fixtures;

public sealed class ConcurrencyBarrier
{
    private readonly object sync = new();
    private readonly int participants;
    private readonly TimeSpan timeout;
    private TaskCompletionSource round = NewRound();
    private int arrived;

    public ConcurrencyBarrier(int participants, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(participants);
        this.participants = participants;
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThan(this.timeout, TimeSpan.Zero);
    }

    public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource current;
        lock (sync)
        {
            current = round;
            // A cancelled/timed-out round stays broken, so no participant waits forever.
            if (!current.Task.IsCompleted)
            {
                arrived++;
                if (arrived == participants)
                {
                    arrived = 0;
                    round = NewRound();
                    current.SetResult();
                }
            }
        }

        try
        {
            await current.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            current.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (TimeoutException exception)
        {
            current.TrySetException(exception);
            throw;
        }
    }

    private static TaskCompletionSource NewRound() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
