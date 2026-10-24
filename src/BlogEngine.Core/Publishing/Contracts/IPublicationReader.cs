namespace BlogEngine.Core.Publishing.Contracts;

public sealed record PublicationAccess(Guid Id, Guid AuthorId, bool Published, bool Deleted, Guid Version);

public interface IPublicationReader
{
    Task<PublicationAccess?> FindAccessAsync(Guid publicationId, CancellationToken cancellationToken);
}
