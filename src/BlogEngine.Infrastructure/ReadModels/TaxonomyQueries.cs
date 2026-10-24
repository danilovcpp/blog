using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

public sealed class TaxonomyQueries(BlogDbContext db)
{
    public async Task<TaxonomyItem> GetAsync(TaxonomyKind kind, Guid id, CancellationToken ct) =>
        (kind == TaxonomyKind.Category ?
            await db.Categories.AsNoTracking().Where(x => x.Id == id).Select(x => new TaxonomyItem(x.Id, x.Name, x.Slug, x.Version)).SingleOrDefaultAsync(ct) :
            await db.Tags.AsNoTracking().Where(x => x.Id == id).Select(x => new TaxonomyItem(x.Id, x.Name, x.Slug, x.Version)).SingleOrDefaultAsync(ct))
        ?? throw new DomainError("resource_not_found");

    public async Task<PageResult<TaxonomyItem>> ListAsync(TaxonomyKind kind, PageRequest page, CancellationToken ct)
    {
        var rows = kind == TaxonomyKind.Category ?
            await db.Categories.FromSqlInterpolated($"SELECT * FROM publishing.categories ORDER BY name COLLATE \"C\", id LIMIT {page.FetchSize} OFFSET {page.Offset}")
                .AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new TaxonomyItem(x.Id, x.Name, x.Slug, x.Version)).ToListAsync(ct) :
            await db.Tags.FromSqlInterpolated($"SELECT * FROM publishing.tags ORDER BY name COLLATE \"C\", id LIMIT {page.FetchSize} OFFSET {page.Offset}")
                .AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new TaxonomyItem(x.Id, x.Name, x.Slug, x.Version)).ToListAsync(ct);
        return PageResult<TaxonomyItem>.FromLookahead(rows, page);
    }
}
