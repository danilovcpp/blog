using BlogEngine.Core.Engagement.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Engagement;

public sealed class LikeWriteStore(BlogDbContext db, IPublicationWriteScope scope) : ILikeWriteStore
{
    public Task<Counters> SetAsync(Guid publicationId, Guid userId, bool desired, CancellationToken ct) =>
        scope.ExecuteAsync(publicationId, async (session, token) =>
        {
            if (!session.Publication.Published || session.Publication.Deleted) { throw new DomainError("resource_not_found"); }
            // RETURNING distinguishes an actual source change from an idempotent repeat.
            var changed = desired
                ? await db.Database.SqlQuery<int>($"INSERT INTO engagement.likes (publication_id,user_id,created_at) VALUES ({publicationId},{userId},{session.UtcNow}) ON CONFLICT (publication_id,user_id) DO NOTHING RETURNING 1 AS \"Value\"").ToListAsync(token)
                : await db.Database.SqlQuery<int>($"DELETE FROM engagement.likes WHERE publication_id={publicationId} AND user_id={userId} RETURNING 1 AS \"Value\"").ToListAsync(token);
            if (changed.Count != 0) { await session.AdjustCountersAsync(new CounterDelta(desired ? 1 : -1, 0), token); }
            return await db.Publications.AsNoTracking().Where(p => p.Id == publicationId)
                .Select(p => new Counters(p.LikeCount, p.ViewCount, desired)).SingleAsync(token);
        }, ct);
}
