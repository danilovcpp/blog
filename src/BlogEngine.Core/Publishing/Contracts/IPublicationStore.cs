using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Publishing.Domain;

namespace BlogEngine.Core.Publishing.Contracts;

public enum PublicationCommand { Edit, Publish, Unpublish, Delete }
public interface IPublicationStore
{
    Task<ClassifiedPublication> CreateAsync(CurrentActor actor, PublicationContent content, CancellationToken cancellationToken);
    Task<ClassifiedPublication?> MutateAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, PublicationCommand command, PublicationContent? content, CancellationToken cancellationToken);
}
