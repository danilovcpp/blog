using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Engagement.Contracts;
using BlogEngine.Core.Publishing.Contracts;

namespace BlogEngine.Core.Engagement.Application;

public sealed class LikeService(ILikeWriteStore store)
{
    public Task<Counters> SetAsync(CurrentActor? actor, Guid publicationId, bool desired, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.Interact);
        return store.SetAsync(publicationId, actor!.Id, desired, ct);
    }
}
