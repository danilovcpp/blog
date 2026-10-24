using BlogEngine.Core.Publishing.Contracts;

namespace BlogEngine.Api.Contracts.Publishing;

public sealed record TaxonomyDto(Guid Id, string Name, string Slug, Guid Version)
{
    public static TaxonomyDto From(TaxonomyItem item) => new(item.Id, item.Name, item.Slug, item.Version);
}
