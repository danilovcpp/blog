using BlogEngine.Core.Accounts.Contracts;

namespace BlogEngine.Core.Publishing.Contracts;

public enum TaxonomyKind { Category, Tag }
public sealed record TaxonomyItem(Guid Id, string Name, string Slug, Guid Version);
public interface ITaxonomyStore
{
    Task<TaxonomyItem> CreateAsync(TaxonomyKind kind, string name, string slug, CancellationToken ct);
    Task<TaxonomyItem?> MutateAsync(TaxonomyKind kind, Guid id, Func<Guid> readVersion, string? name, CancellationToken ct);
}
