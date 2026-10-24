using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;

namespace BlogEngine.Core.Publishing.Application;

public sealed class PublicationService(IPublicationStore store)
{
    public Task<Guid> CreateAsync(CurrentActor actor, PublicationContent content, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.CreatePublication);
        return store.CreateAsync(actor, content.Validate(), ct);
    }
    public Task MutateAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, PublicationCommand command, PublicationContent? content, CancellationToken ct) =>
        store.MutateAsync(actor, id, readVersion, command, content, ct);
}
