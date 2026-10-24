using System.Data;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Publishing;

public sealed class TaxonomyStore(BlogDbContext db) : ITaxonomyStore
{
    public async Task<TaxonomyItem> CreateAsync(TaxonomyKind kind, string name, string slug, CancellationToken ct)
    {
        TaxonomyItem result;
        if (kind == TaxonomyKind.Category)
        {
            var entity = new Category(name, slug); db.Categories.Add(entity); result = new(entity.Id, entity.Name, entity.Slug, entity.Version);
        }
        else
        {
            var entity = new Tag(name, slug); db.Tags.Add(entity); result = new(entity.Id, entity.Name, entity.Slug, entity.Version);
        }
        await db.SaveChangesAsync(ct);
        return result;
    }
    public async Task<TaxonomyItem?> MutateAsync(TaxonomyKind kind, Guid id, Func<Guid> readVersion, string? name, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        TaxonomyItem? result = null;
        if (kind == TaxonomyKind.Category)
        {
            var entity = await db.Categories.FromSqlInterpolated($"SELECT * FROM publishing.categories WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw new DomainError("resource_not_found");
            await db.Entry(entity).ReloadAsync(ct);
            if (readVersion() != entity.Version) { throw new DomainError("concurrency_conflict"); }
            if (name is null) { db.Categories.Remove(entity); }
            else { entity.Rename(name); result = new(entity.Id, entity.Name, entity.Slug, entity.Version); }
        }
        else
        {
            var entity = await db.Tags.FromSqlInterpolated($"SELECT * FROM publishing.tags WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw new DomainError("resource_not_found");
            await db.Entry(entity).ReloadAsync(ct);
            if (readVersion() != entity.Version) { throw new DomainError("concurrency_conflict"); }
            if (name is null) { db.Tags.Remove(entity); }
            else { entity.Rename(name); result = new(entity.Id, entity.Name, entity.Slug, entity.Version); }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
}
