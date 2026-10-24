using BlogEngine.Core.Publishing.Contracts;

namespace BlogEngine.Core.Engagement.Contracts;

// Source and aggregate are committed together under Publishing's lock and Published guard.
public interface ILikeWriteStore
{
    Task<Counters> SetAsync(Guid publicationId, Guid userId, bool desired, CancellationToken ct);
}
