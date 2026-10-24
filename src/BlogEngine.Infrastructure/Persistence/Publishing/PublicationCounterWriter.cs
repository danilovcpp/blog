using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlogEngine.Infrastructure.Persistence.Publishing;

internal static class PublicationCounterWriter
{
    // Only the locked write session can reach this writer. The database remains the final bigint/CHECK guard.
    internal static async Task AdjustAsync(BlogDbContext db, Publication publication, CounterDelta delta, IDbContextTransaction transaction, CancellationToken ct)
    {
        if (!ReferenceEquals(db.Database.CurrentTransaction, transaction)) { throw new InvalidOperationException("The held publication write transaction is required."); }
        delta.Apply(publication.LikeCount, publication.ViewCount);
        if (delta == default) { return; }
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE publishing.publications SET like_count = like_count + {delta.Likes}, view_count = view_count + {delta.Views} WHERE id = {publication.Id}", ct);
        // Keep the held session coherent for another bounded delta; no content/version/timestamp is changed.
        await db.Entry(publication).ReloadAsync(ct);
    }
}
