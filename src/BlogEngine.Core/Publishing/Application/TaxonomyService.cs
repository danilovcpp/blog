using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Publishing.Application;

public sealed class TaxonomyService(ITaxonomyStore store)
{
    public Task<TaxonomyItem> CreateAsync(CurrentActor actor, TaxonomyKind kind, string name, string slug, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.ManageTaxonomy);
        TextRules.RequireSlug(slug);
        return store.CreateAsync(kind, TextRules.RequireTrimmed(name, 80, "name"), slug, ct);
    }
    public Task<TaxonomyItem?> MutateAsync(CurrentActor actor, TaxonomyKind kind, Guid id, Func<Guid> readVersion, string? name, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.ManageTaxonomy);
        return store.MutateAsync(kind, id, readVersion, name, ct);
    }
}
