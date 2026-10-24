using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Engagement.Application;
using BlogEngine.Core.Engagement.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Engagement;

[Trait("Category", "Likes")]
public sealed class LikeTests
{
    [Fact]
    public async Task VerifiedReaderUsesOwnIdentityAndDesiredStateIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = new CurrentActor(Guid.NewGuid(), []); var publicationId = Guid.NewGuid();
        var store = new Store(publicationId, actor.Id); var service = new LikeService(store);
        foreach (var desired in new[] { true, true, false, false, true })
        {
            var result = await service.SetAsync(actor, publicationId, desired, ct);
            Assert.Equal(desired ? 1 : 0, result.LikeCount); Assert.Equal(desired, result.IsLikedByCurrentUser);
            Assert.Equal(7, result.ViewCount);
        }
        Assert.Equal(new[] { 1, 0, -1, 0, 1 }, store.Deltas);
        Assert.Equal("authentication_required", (await Assert.ThrowsAsync<DomainError>(() => service.SetAsync(null, publicationId, true, ct))).Code);
        Assert.Equal(5, store.Calls);
        store.Published = false;
        Assert.Equal("resource_not_found", (await Assert.ThrowsAsync<DomainError>(() => service.SetAsync(actor, publicationId, false, ct))).Code);
    }

    [Fact]
    public void BoundedDeltasRejectNegativeAndOverflowingBigint()
    {
        Assert.Equal((1L, 7L), new CounterDelta(1, 0).Apply(0, 7));
        Assert.Equal((0L, 7L), new CounterDelta(-1, 0).Apply(1, 7));
        Assert.Equal((long.MaxValue, long.MaxValue), new CounterDelta(0, 0).Apply(long.MaxValue, long.MaxValue));
        Assert.Throws<InvalidOperationException>(() => new CounterDelta(-1, 0).Apply(0, 0));
        Assert.Throws<InvalidOperationException>(() => new CounterDelta(0, 0).Apply(0, -1));
        Assert.Throws<OverflowException>(() => new CounterDelta(1, 0).Apply(long.MaxValue, 0));
        Assert.Throws<OverflowException>(() => new CounterDelta(0, 1).Apply(0, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CounterDelta(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CounterDelta(0, -1));
    }

    private sealed class Store(Guid publicationId, Guid userId) : ILikeWriteStore
    {
        private bool liked;
        public bool Published { get; set; } = true;
        public int Calls { get; private set; }
        public List<int> Deltas { get; } = [];
        public Task<Counters> SetAsync(Guid publication, Guid user, bool desired, CancellationToken ct)
        {
            Assert.Equal(publicationId, publication); Assert.Equal(userId, user); ct.ThrowIfCancellationRequested();
            if (!Published) { throw new DomainError("resource_not_found"); }
            Calls++; Deltas.Add((desired ? 1 : 0) - (liked ? 1 : 0)); liked = desired;
            return Task.FromResult(new Counters(liked ? 1 : 0, 7, liked));
        }
    }
}
