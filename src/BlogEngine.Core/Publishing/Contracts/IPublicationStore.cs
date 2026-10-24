using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Publishing.Domain;

namespace BlogEngine.Core.Publishing.Contracts;

public enum PublicationCommand { Edit, Publish, Unpublish, Delete }
public interface IPublicationStore
{
    Task<Guid> CreateAsync(CurrentActor actor, PublicationContent content, CancellationToken cancellationToken);
    Task MutateAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, PublicationCommand command, PublicationContent? content, CancellationToken cancellationToken);
}
