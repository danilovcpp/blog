using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Discussions.Domain;

public enum CommentState : short { Visible, Hidden, Deleted }

public sealed class Comment
{
    private Comment() { }
    public Guid Id { get; private set; }
    public Guid PublicationId { get; private set; }
    public Guid AuthorId { get; private set; }
    public Guid? ParentId { get; private set; }
    public short Depth { get; private set; }
    public CommentState State { get; private set; }
    public string? Text { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? EditedAt { get; private set; }
    public Guid Version { get; private set; }
    public long VisibleDescendantCount { get; private set; }

    public static Comment Create(Guid publicationId, Guid authorId, Comment? parent, string content, DateTimeOffset now)
    {
        RequireUtc(now);
        if (parent is not null && (parent.PublicationId != publicationId || parent.State != CommentState.Visible || parent.Depth >= 5))
        { throw new DomainError("invalid_state"); }
        return new Comment
        {
            Id = Guid.NewGuid(),
            PublicationId = publicationId,
            AuthorId = authorId,
            ParentId = parent?.Id,
            Depth = (short)((parent?.Depth ?? 0) + 1),
            State = CommentState.Visible,
            Text = TextRules.RequireTrimmed(content, 5000, "content"),
            CreatedAt = now,
            UpdatedAt = now,
            Version = Guid.NewGuid(),
        };
    }

    public void DemandOwner(CurrentActor actor)
    {
        AccessPolicy.Demand(actor, ActorAction.Interact);
        if (actor.Id != AuthorId && !actor.HasRole(LocalRole.Admin)) { throw new DomainError("forbidden"); }
    }
    public void DemandEdit(CurrentActor actor)
    {
        DemandOwner(actor);
        if (State == CommentState.Hidden && !actor.HasRole(LocalRole.Admin)) { throw new DomainError("forbidden"); }
    }
    public void Edit(CurrentActor actor, string content, DateTimeOffset now)
    {
        DemandEdit(actor);
        if (State == CommentState.Deleted) { throw new DomainError("invalid_state"); }
        RequireUtc(now);
        Text = TextRules.RequireTrimmed(content, 5000, "content"); EditedAt = now; Changed(now);
    }
    public void Delete(CurrentActor actor, DateTimeOffset now)
    {
        DemandOwner(actor);
        if (State == CommentState.Deleted) { return; }
        RequireUtc(now); State = CommentState.Deleted; Text = null; Changed(now);
    }
    public void DemandModeration(CurrentActor actor) => AccessPolicy.Demand(actor, ActorAction.ModerateComments);
    public void RequireVersion(Guid version)
    { if (Version != version) { throw new DomainError("concurrency_conflict"); } }
    public void Hide(CurrentActor actor, DateTimeOffset now) => SetVisibility(actor, CommentState.Hidden, now);
    public void Restore(CurrentActor actor, DateTimeOffset now) => SetVisibility(actor, CommentState.Visible, now);
    private void SetVisibility(CurrentActor actor, CommentState desired, DateTimeOffset now)
    {
        DemandModeration(actor);
        if (State == CommentState.Deleted) { throw new DomainError("invalid_state"); }
        if (State == desired) { return; }
        RequireUtc(now); State = desired; Changed(now);
    }
    private void Changed(DateTimeOffset now) { UpdatedAt = now; Version = Guid.NewGuid(); }
    private static void RequireUtc(DateTimeOffset now)
    { if (now.Offset != TimeSpan.Zero) { throw new ArgumentException("UTC is required.", nameof(now)); } }
}
