using BlogEngine.Core.Shared;

namespace BlogEngine.Api.Contracts;

public static class ResourceVersion
{
    // Call only after the resource access guard, so version errors cannot expose hidden resources.
    public static Guid Read(HttpRequest request)
    {
        var values = request.Headers["X-Resource-Version"];
        if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out var version))
        {
            throw DomainError.Validation("X-Resource-Version");
        }
        return version;
    }

    public static void RequireCurrent(Guid supplied, Guid current)
    {
        if (supplied != current) { throw new DomainError("concurrency_conflict"); }
    }
}
