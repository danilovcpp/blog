using BlogEngine.Core.Discussions.Domain;

namespace BlogEngine.Core.Discussions.Contracts;

// Callbacks run under Publishing's single transaction/lock, after its Published guard.
public interface ICommentWriteStore
{
    Task<CommentSnapshot> CreateAsync(Guid publicationId, Guid? parentId, Func<Comment?, DateTimeOffset, Comment> create, CancellationToken ct);
    Task<CommentSnapshot> MutateAsync(Guid commentId, Action<Comment, DateTimeOffset> mutation, CancellationToken ct);
}

public sealed record CommentSnapshot(Guid Id, Guid PublicationId, Guid? ParentId, int Depth, string State, string? Content,
    Guid AuthorId, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, Guid Version)
{
    public static CommentSnapshot From(Comment comment) => new(comment.Id, comment.PublicationId, comment.ParentId, comment.Depth,
        comment.State.ToString(), comment.Text, comment.AuthorId, comment.CreatedAt, comment.EditedAt, comment.Version);
}

public sealed record CommentItem(Guid Id, Guid PublicationId, Guid? ParentId, int Depth, string State, string? Content,
    Guid? AuthorId, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, bool HasReplies);
