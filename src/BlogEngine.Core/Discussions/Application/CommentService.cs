using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;

namespace BlogEngine.Core.Discussions.Application;

public sealed class CommentService(ICommentWriteStore store)
{
    public Task<CommentSnapshot> CreateAsync(CurrentActor actor, Guid publicationId, Guid? parentId, string content, CancellationToken ct)
    {
        AccessPolicy.Demand(actor, ActorAction.Interact);
        return store.CreateAsync(publicationId, parentId, (parent, now) => Comment.Create(publicationId, actor.Id, parent, content, now), ct);
    }
    public Task<CommentSnapshot> EditAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, string content, CancellationToken ct) =>
        store.MutateAsync(id, (comment, now) =>
        {
            comment.DemandEdit(actor); comment.RequireVersion(readVersion()); comment.Edit(actor, content, now);
        }, ct);
    public Task<CommentSnapshot> DeleteAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, CancellationToken ct) =>
        store.MutateAsync(id, (comment, now) =>
        {
            comment.DemandOwner(actor); comment.RequireVersion(readVersion()); comment.Delete(actor, now);
        }, ct);
}
