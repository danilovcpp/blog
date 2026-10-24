using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;

namespace BlogEngine.Core.Discussions.Application;

public sealed class ModerationService(ICommentWriteStore store)
{
    public Task<CommentSnapshot> HideAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, CancellationToken ct) =>
        TransitionAsync(actor, id, readVersion, (comment, now) => comment.Hide(actor, now), ct);
    public Task<CommentSnapshot> RestoreAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, CancellationToken ct) =>
        TransitionAsync(actor, id, readVersion, (comment, now) => comment.Restore(actor, now), ct);

    private Task<CommentSnapshot> TransitionAsync(CurrentActor actor, Guid id, Func<Guid> readVersion,
        Action<Comment, DateTimeOffset> transition, CancellationToken ct) => store.MutateAsync(id, (comment, now) =>
        {
            comment.DemandModeration(actor); comment.RequireVersion(readVersion()); transition(comment, now);
        }, ct);
}
