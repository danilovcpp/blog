namespace BlogEngine.Core.ReadModels;

public interface IPublicFeedReader
{
    Task<PageResult<PublicationFeed>> ReadAsync(PublicationFeedRequest request, Guid? actorId, CancellationToken cancellationToken);
}
