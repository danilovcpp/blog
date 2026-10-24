using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.Api.Contracts.Publishing;

public static class PublishingValidation
{
    public static PageRequest Page(HttpRequest request)
    {
        static int Number(HttpRequest request, string name, int fallback)
        {
            var values = request.Query[name];
            if (values.Count == 0) { return fallback; }
            if (values.Count != 1 || !int.TryParse(values[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            { throw DomainError.Validation(name); }
            return number;
        }
        return new(Number(request, "page", 1), Number(request, "pageSize", 20));
    }
    public static PublicationState? State(HttpRequest request)
    {
        var values = request.Query["state"];
        if (values.Count == 0) { return null; }
        return values.Count == 1 ? values[0] switch
        {
            "Draft" => PublicationState.Draft,
            "Published" => PublicationState.Published,
            _ => throw DomainError.Validation("state"),
        } : throw DomainError.Validation("state");
    }
}
