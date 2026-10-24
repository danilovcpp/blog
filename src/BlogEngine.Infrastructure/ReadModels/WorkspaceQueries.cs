using System.Linq.Expressions;
using BlogEngine.Core.Accounts.Contracts;
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
            actorId != null && db.Likes.Any(l => l.PublicationId == p.Id && l.UserId == actorId));

    public async Task<ClassifiedPublication> GetAsync(CurrentActor actor, Guid id, CancellationToken ct)
    {
        var access = await db.Publications.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new PublicationAccess(p.Id, p.AuthorId, p.State == PublicationState.Published, p.DeletedAt != null, p.Version)).SingleOrDefaultAsync(ct)
            ?? throw new DomainError("resource_not_found");
        AccessPolicy.DemandPublicationEdit(actor, access.AuthorId, access.Published, access.Deleted);
        var row = await db.Publications.AsNoTracking().Where(p => p.Id == id && p.DeletedAt == null).Select(Projection(actor.Id, true)).SingleOrDefaultAsync(ct)
            ?? throw new DomainError("resource_not_found");
        return (await ClassifyAsync([row], ct))[0];
    }

    public async Task<PageResult<ClassifiedPublication>> ListAsync(CurrentActor actor, PageRequest page, PublicationState? state, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.CreatePublication);
        var admin = actor.HasRole(LocalRole.Admin);
        var stateCode = state is null ? (short?)null : (short)state;
        var rows = await db.Publications.FromSqlInterpolated($"""
            SELECT * FROM publishing.publications WHERE deleted_at IS NULL
            AND ({admin} OR author_id = {actor.Id}) AND ({stateCode}::smallint IS NULL OR state = {stateCode})
            ORDER BY updated_at DESC, id DESC LIMIT {page.FetchSize} OFFSET {page.Offset}
            """).AsNoTracking().OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id).Select(Projection(actor.Id, false)).ToListAsync(ct);
        var result = PageResult<PublicationSnapshot>.FromLookahead(rows, page);
        return new(await ClassifyAsync(result.Items, ct), page.Page, page.PageSize, result.HasNextPage);
    }

    internal async Task<IReadOnlyList<ClassifiedPublication>> ClassifyAsync(IReadOnlyList<PublicationSnapshot> rows, CancellationToken ct)
    {
        if (rows.Count == 0) { return []; }
        var ids = rows.Select(x => x.Id).ToArray();
        var categories = await (from link in db.PublicationCategories.AsNoTracking()
                                join item in db.Categories on link.CategoryId equals item.Id
                                where ids.Contains(link.PublicationId)
                                orderby item.Name, item.Id
                                select new { link.PublicationId, Item = new TaxonomyItem(item.Id, item.Name, item.Slug, item.Version) }).ToListAsync(ct);
        var tags = await (from link in db.PublicationTags.AsNoTracking()
                          join item in db.Tags on link.TagId equals item.Id
                          where ids.Contains(link.PublicationId)
                          orderby item.Name, item.Id
                          select new { link.PublicationId, Item = new TaxonomyItem(item.Id, item.Name, item.Slug, item.Version) }).ToListAsync(ct);
        var categoryLookup = categories.ToLookup(x => x.PublicationId, x => x.Item);
        var tagLookup = tags.ToLookup(x => x.PublicationId, x => x.Item);
        return rows.Select(row => new ClassifiedPublication(row, categoryLookup[row.Id].ToArray(), tagLookup[row.Id].ToArray())).ToArray();
    }
}
