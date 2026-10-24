namespace BlogEngine.IntegrationTests.Fixtures;

// Controls domain/JWT observation time. Timeouts deliberately use real monotonic time.
public sealed class AdjustableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
{
    private readonly object sync = new();
    private long utcTicks = initialUtc.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
        lock (sync)
        {
            var next = GetUtcNow().Add(amount);
            Interlocked.Exchange(ref utcTicks, next.UtcTicks);
        }
    }
}
