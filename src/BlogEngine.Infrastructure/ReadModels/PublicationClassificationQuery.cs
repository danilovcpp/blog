using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.ReadModels;

internal sealed record ClassificationBatch(ILookup<Guid, TaxonomyItem> Categories, ILookup<Guid, TaxonomyItem> Tags);

internal static class PublicationClassificationQuery
{
    internal static async Task<ClassificationBatch> LoadAsync(BlogDbContext db, Guid[] ids, CancellationToken ct)
    {
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
        return new(categories.ToLookup(x => x.PublicationId, x => x.Item), tags.ToLookup(x => x.PublicationId, x => x.Item));
    }
}
