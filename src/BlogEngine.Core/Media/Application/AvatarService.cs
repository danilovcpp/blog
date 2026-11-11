using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;

namespace BlogEngine.Core.Media.Application;

public sealed record AvatarSnapshot(Guid? FileId, Guid Version)
{
    public AvatarReference? Avatar => AvatarReference.From(FileId);
}
public sealed record AvatarProfile(CurrentActor Actor, AvatarSnapshot State);

public interface IAvatarStateStore
{
    Task<AvatarProfile> ProfileAsync(Guid ownerId, CancellationToken ct);
    Task<AvatarSnapshot> DeleteAsync(Guid ownerId, Guid version, CancellationToken ct);
}

public sealed class AvatarService(IAvatarStateStore store)
{
    public Task<AvatarProfile> ProfileAsync(CurrentActor actor, CancellationToken ct) => store.ProfileAsync(actor.Id, ct);
    public Task<AvatarSnapshot> DeleteAsync(CurrentActor actor, Guid version, CancellationToken ct) => store.DeleteAsync(actor.Id, version, ct);
}
