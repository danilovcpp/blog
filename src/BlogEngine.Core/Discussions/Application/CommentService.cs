using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Shared;

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
            comment.DemandEdit(actor); RequireVersion(comment, readVersion()); comment.Edit(actor, content, now);
        }, ct);
    public Task<CommentSnapshot> DeleteAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, CancellationToken ct) =>
        store.MutateAsync(id, (comment, now) =>
        {
            comment.DemandOwner(actor); RequireVersion(comment, readVersion()); comment.Delete(actor, now);
        }, ct);
    private static void RequireVersion(Comment comment, Guid version)
    { if (comment.Version != version) { throw new DomainError("concurrency_conflict"); } }
}
