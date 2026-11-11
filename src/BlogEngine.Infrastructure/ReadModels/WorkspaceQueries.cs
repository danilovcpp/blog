using System.Data;
using System.Linq.Expressions;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class WorkspaceQueries(BlogDbContext db)
{
    internal Expression<Func<Publication, PublicationSnapshot>> Projection(Guid? actorId, bool markdown) => p =>
        new PublicationSnapshot(p.Id, p.Title, p.Slug, markdown ? p.Markdown : null, p.Description, p.CoverUrl, p.AuthorId, p.State,
            p.CreatedAt, p.UpdatedAt, p.FirstPublishedAt, p.Version, p.LikeCount, p.ViewCount,
            actorId != null && db.Likes.Any(l => l.PublicationId == p.Id && l.UserId == actorId),
            db.PublicationUsages.Where(u => u.PublicationId == p.Id && u.Role == MediaUsageRole.Cover).Select(u => (Guid?)u.FileId).SingleOrDefault());

    public async Task<ClassifiedPublication> GetAsync(CurrentActor actor, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var access = await db.Publications.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new PublicationAccess(p.Id, p.AuthorId, p.State == PublicationState.Published, p.DeletedAt != null, p.Version)).SingleOrDefaultAsync(ct)
            ?? throw new DomainError("resource_not_found");
        AccessPolicy.DemandPublicationEdit(actor, access.AuthorId, access.Published, access.Deleted);
        var result = await SnapshotAsync(id, actor.Id, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    // Called by command stores before commit, and by reads inside their coherent snapshot.
    internal async Task<ClassifiedPublication> SnapshotAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var row = await db.Publications.AsNoTracking().Where(p => p.Id == id && p.DeletedAt == null).Select(Projection(actorId, true)).SingleOrDefaultAsync(ct)
            ?? throw new DomainError("resource_not_found");
        return (await ClassifyAsync([row], ct))[0];
    }

    public async Task<PageResult<ClassifiedPublication>> ListAsync(CurrentActor actor, PageRequest page, PublicationState? state, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.CreatePublication);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var admin = actor.HasRole(LocalRole.Admin);
        var stateCode = state is null ? (short?)null : (short)state;
        var rows = await db.Publications.FromSqlInterpolated($"""
            SELECT * FROM publishing.publications WHERE deleted_at IS NULL
            AND ({admin} OR author_id = {actor.Id}) AND ({stateCode}::smallint IS NULL OR state = {stateCode})
            ORDER BY updated_at DESC, id DESC LIMIT {page.FetchSize} OFFSET {page.Offset}
            """).AsNoTracking().OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id).Select(Projection(actor.Id, false)).ToListAsync(ct);
        var result = PageResult<PublicationSnapshot>.FromLookahead(rows, page);
        var classified = await ClassifyAsync(result.Items, ct, details: false);
        await tx.CommitAsync(ct);
        return new(classified, page.Page, page.PageSize, result.HasNextPage);
    }

    internal async Task<IReadOnlyList<ClassifiedPublication>> ClassifyAsync(IReadOnlyList<PublicationSnapshot> rows, CancellationToken ct, bool details = true)
    {
        if (rows.Count == 0) { return []; }
        var classification = await PublicationClassificationQuery.LoadAsync(db, rows.Select(x => x.Id).ToArray(), ct);
        var media = details ? await PublicationMediaProjection.LoadAsync(db, rows.Select(x => x.Id).ToArray(), ct)
            : rows.ToDictionary(row => row.Id, row => new PublicationMediaSnapshot(row.CoverFileId, [], []));
        return rows.Select(row => new ClassifiedPublication(row with
        {
            CoverFileId = media[row.Id].CoverFileId,
            CoverUrl = media[row.Id].CoverFileId is { } cover ? PublicationMediaUrls.Display(row.Id, cover) : row.CoverUrl
        },
            classification.Categories[row.Id].ToArray(), classification.Tags[row.Id].ToArray(), media[row.Id])).ToArray();
    }
}
