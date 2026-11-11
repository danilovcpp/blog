using System.Data;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class PublicationDetailQuery(BlogDbContext db, WorkspaceQueries queries)
{
    public async Task<ClassifiedPublication> GetAsync(Guid? id, string? slug, Guid? actorId, CancellationToken ct)
    {
        if (slug is not null) { TextRules.RequireSlug(slug); }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var row = await db.Publications.AsNoTracking().Where(p => p.State == PublicationState.Published && p.DeletedAt == null &&
            (id != null ? p.Id == id : p.Slug == slug)).Select(queries.Projection(actorId, true)).SingleOrDefaultAsync(ct)
            ?? throw new DomainError("resource_not_found");
        var result = (await queries.ClassifyAsync([row], ct))[0];
        await tx.CommitAsync(ct);
        return result;
    }
}
