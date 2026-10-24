using BlogEngine.Api.Contracts.Publishing;
using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;

namespace BlogEngine.Api.Contracts.Reading;

public static class FeedRequest
{
    public static PublicationFeedRequest Parse(HttpRequest request)
    {
        foreach (var name in request.Query.Keys)
        {
            if (name is not ("page" or "pageSize" or "sort" or "categoryIds" or "tagIds")) { throw DomainError.Validation(name); }
        }
        var sorts = request.Query["sort"];
        var sort = sorts.Count switch
        {
            0 => PublicationSort.Recent,
            1 => sorts[0] switch { "recent" => PublicationSort.Recent, "popular" => PublicationSort.Popular, _ => throw DomainError.Validation("sort") },
            _ => throw DomainError.Validation("sort"),
        };
        return new(PublishingValidation.Page(request), sort, Ids(request, "categoryIds", 5), Ids(request, "tagIds", 20));
    }

    private static Guid[] Ids(HttpRequest request, string field, int maximum)
    {
        var values = request.Query[field];
        if (values.Count > maximum) { throw DomainError.Validation(field); }
        return values.Select(value => Guid.TryParseExact(value, "D", out var id) ? id : throw DomainError.Validation(field)).ToArray();
    }
}
