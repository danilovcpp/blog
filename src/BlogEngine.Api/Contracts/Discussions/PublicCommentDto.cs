using System.Text.Json.Serialization;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.ReadModels;

namespace BlogEngine.Api.Contracts.Discussions;

[JsonDerivedType(typeof(VisibleCommentDto))]
[JsonDerivedType(typeof(CommentPlaceholderDto))]
public abstract record PublicCommentDto(Guid Id, Guid PublicationId, Guid? ParentId, int Depth,
    DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, bool HasReplies)
{
    public static PublicCommentDto From(CommentItem item) => item.State == "Visible"
        ? new VisibleCommentDto(item.Id, item.PublicationId, item.ParentId, item.Depth, item.CreatedAt, item.EditedAt, item.HasReplies, item.Content!, new AuthorRef(item.AuthorId!.Value))
        : new CommentPlaceholderDto(item.Id, item.PublicationId, item.ParentId, item.Depth, item.CreatedAt, item.EditedAt, item.HasReplies, Enum.Parse<PlaceholderState>(item.State));
}

public enum VisibleCommentState { Visible }
public enum PlaceholderState { Hidden, Deleted }

public sealed record VisibleCommentDto(Guid Id, Guid PublicationId, Guid? ParentId, int Depth, DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt, bool HasReplies, string Content, AuthorRef Author)
    : PublicCommentDto(Id, PublicationId, ParentId, Depth, CreatedAt, EditedAt, HasReplies)
{
    public VisibleCommentState State => VisibleCommentState.Visible;
}

public sealed record CommentPlaceholderDto(Guid Id, Guid PublicationId, Guid? ParentId, int Depth, DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt, bool HasReplies, PlaceholderState State)
    : PublicCommentDto(Id, PublicationId, ParentId, Depth, CreatedAt, EditedAt, HasReplies)
{
    public string? Content => null;
    public AuthorRef? Author => null;
}
