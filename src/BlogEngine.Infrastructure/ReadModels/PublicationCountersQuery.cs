using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class PublicationCountersQuery(BlogDbContext db)
{
    public async Task<Counters> GetAsync(Guid id, Guid? actorId, CancellationToken ct) =>
        await db.Publications.AsNoTracking().Where(p => p.Id == id && p.State == PublicationState.Published && p.DeletedAt == null)
            .Select(p => new Counters(p.LikeCount, p.ViewCount, actorId != null && db.Likes.Any(l => l.PublicationId == p.Id && l.UserId == actorId)))
            .SingleOrDefaultAsync(ct) ?? throw new DomainError("resource_not_found");
}
