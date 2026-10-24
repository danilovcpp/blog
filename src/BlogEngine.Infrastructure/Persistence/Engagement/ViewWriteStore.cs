using BlogEngine.Core.Engagement.Contracts;
using BlogEngine.Core.Engagement.Domain;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Engagement;

public sealed class ViewWriteStore(BlogDbContext db, IPublicationWriteScope scope) : IViewWriteStore
{
    public Task<ViewRegistration> RegisterAsync(Guid publicationId, VisitorIdentity visitor, CancellationToken ct) =>
        scope.ExecuteAsync(publicationId, async (session, token) =>
        {
            if (!session.Publication.Published || session.Publication.Deleted) { throw new DomainError("resource_not_found"); }
            var key = visitor.Key.ToArray(); var kind = (short)visitor.Kind; var now = session.UtcNow;
            var window = ViewDeduplicationRecord.CountingWindow;
            var changed = await db.Database.SqlQuery<int>($"""
                INSERT INTO engagement.view_deduplication_records (publication_id,visitor_kind,visitor_key,last_counted_at)
                VALUES ({publicationId},{kind},{key},{now})
                ON CONFLICT (publication_id,visitor_kind,visitor_key) DO UPDATE SET last_counted_at=EXCLUDED.last_counted_at
                WHERE view_deduplication_records.last_counted_at <= {now} - {window}
                RETURNING 1 AS "Value"
                """).ToListAsync(token);
            if (changed.Count != 0) { await session.AdjustCountersAsync(new CounterDelta(0, 1), token); }
            var count = await db.Publications.AsNoTracking().Where(p => p.Id == publicationId).Select(p => p.ViewCount).SingleAsync(token);
            return new ViewRegistration(changed.Count != 0, count);
        }, ct);
}
