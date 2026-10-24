using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;

namespace BlogEngine.Api.Contracts.Discussions;

public sealed record ModerationCommentDto(Guid Id, Guid PublicationId, Guid? ParentId, int Depth, CommentEditState State, string? Content,
    AuthorRef Author, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, Guid Version)
{
    public static ModerationCommentDto From(CommentSnapshot item) => new(item.Id, item.PublicationId, item.ParentId, item.Depth,
        Enum.Parse<CommentEditState>(item.State), item.Content, new AuthorRef(item.AuthorId), item.CreatedAt, item.EditedAt, item.Version);
}

public static class ModerationRequest
{
    public static (Guid? PublicationId, CommentState? State) Read(HttpRequest request)
    {
        foreach (var name in request.Query.Keys)
        { if (name is not ("page" or "pageSize" or "publicationId" or "state")) { throw DomainError.Validation(name); } }
        Guid? publicationId = null;
        var publications = request.Query["publicationId"];
        if (publications.Count > 0)
        {
            if (publications.Count != 1 || !Guid.TryParseExact(publications[0], "D", out var id)) { throw DomainError.Validation("publicationId"); }
            publicationId = id;
        }
        var states = request.Query["state"];
        CommentState? state = states.Count == 0 ? null : states.Count == 1 ? states[0] switch
        {
            "Visible" => CommentState.Visible,
            "Hidden" => CommentState.Hidden,
            "Deleted" => CommentState.Deleted,
            _ => throw DomainError.Validation("state"),
        } : throw DomainError.Validation("state");
        return (publicationId, state);
    }
}
