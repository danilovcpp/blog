using System.ComponentModel.DataAnnotations;
using BlogEngine.Core.Shared;

namespace BlogEngine.Api.Contracts.Discussions;

public sealed record CommentCreate([property: MinLength(1), MaxLength(5000)] string Content, Guid? ParentId = null);
public sealed record CommentUpdate([property: MinLength(1), MaxLength(5000)] string Content);

public static class CommentQuery
{
    public static Guid? ParentId(HttpRequest request)
    {
        foreach (var name in request.Query.Keys)
        { if (name is not ("page" or "pageSize" or "parentId")) { throw DomainError.Validation(name); } }
        var values = request.Query["parentId"];
        if (values.Count == 0) { return null; }
        if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out var id)) { throw DomainError.Validation("parentId"); }
        return id;
    }
}
