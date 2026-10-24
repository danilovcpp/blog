using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Publishing.Domain;

public static class PublicationClassification
{
    public static void Validate(IReadOnlyList<Guid> categories, IReadOnlyList<Guid> tags)
    {
        Check(categories, 5, "categoryIds"); Check(tags, 20, "tagIds");
    }
    private static void Check(IReadOnlyList<Guid> ids, int maximum, string field)
    {
        if (ids.Count > maximum || ids.Distinct().Count() != ids.Count) { throw DomainError.Validation(field); }
    }
}

public sealed record PublicationCategory(Guid PublicationId, Guid CategoryId);
public sealed record PublicationTag(Guid PublicationId, Guid TagId);
