using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.ReadModels;

namespace BlogEngine.Api.Contracts.Discussions;

public enum CommentEditState { Visible, Hidden, Deleted }

public sealed record CommentEditContextDto(Guid Id, Guid PublicationId, Guid? ParentId, int Depth, CommentEditState State, string? Content,
    AuthorRef Author, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, Guid Version)
{
    public static CommentEditContextDto From(CommentSnapshot item) => new(item.Id, item.PublicationId, item.ParentId, item.Depth,
        Enum.Parse<CommentEditState>(item.State), item.Content, new AuthorRef(item.AuthorId), item.CreatedAt, item.EditedAt, item.Version);
}
