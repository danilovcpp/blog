using BlogEngine.IntegrationTests.Fixtures;

namespace BlogEngine.UnitTests.Setup;

public sealed class FixturePrimitiveTests
{
    [Fact]
    public async Task BarrierReleasesOnlyWhenEveryParticipantArrivesAndCanBeReused()
    {
        var barrier = new ConcurrencyBarrier(2);
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var round = 0; round < 2; round++)
        {
            var first = barrier.SignalAndWaitAsync(cancellationToken);
            Assert.False(first.IsCompleted);
            await barrier.SignalAndWaitAsync(cancellationToken);
            await first;
        }
    }

    [Fact]
    public async Task CancelledWaitBreaksTheRoundForAllParticipants()
    {
        var barrier = new ConcurrencyBarrier(3);
        using var cancellation = new CancellationTokenSource();
        var first = barrier.SignalAndWaitAsync(cancellation.Token);
        var second = barrier.SignalAndWaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            barrier.SignalAndWaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BarrierTimesOutInsteadOfHanging()
    {
        var barrier = new ConcurrencyBarrier(2, TimeSpan.Zero);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            barrier.SignalAndWaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BarrierRequiresPositiveParticipantCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrencyBarrier(0));
    }

    [Fact]
    public async Task AlreadyCancelledParticipantCannotReleaseTheBarrier()
    {
        var barrier = new ConcurrencyBarrier(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => barrier.SignalAndWaitAsync(cancellation.Token));
    }

    [Fact]
    public void ClockNormalizesUtcAndAdvancesWithoutWaiting()
    {
        var initial = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(6));
        var clock = new AdjustableTimeProvider(initial);
        Assert.Equal(TimeSpan.Zero, clock.GetUtcNow().Offset);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(initial.AddDays(1), clock.GetUtcNow());
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromTicks(-1)));
    }
}
