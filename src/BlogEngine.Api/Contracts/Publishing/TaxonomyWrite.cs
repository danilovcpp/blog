namespace BlogEngine.Api.Contracts.Publishing;

public sealed record TaxonomyCreate(string Name, string Slug);
public sealed record TaxonomyRename(string Name);
