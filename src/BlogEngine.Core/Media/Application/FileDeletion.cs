using BlogEngine.Core.Accounts.Contracts;

namespace BlogEngine.Core.Media.Application;

public interface IFileDeletionStore
{
    Task DeleteAsync(Guid actorId, Guid fileId, CancellationToken ct);
}

public sealed class FileDeletion(IFileDeletionStore store)
{
    public Task DeleteAsync(CurrentActor actor, Guid fileId, CancellationToken ct) => store.DeleteAsync(actor.Id, fileId, ct);
}
