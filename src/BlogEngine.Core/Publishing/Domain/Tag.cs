using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Publishing.Domain;

public sealed class Tag
{
    public Tag(string name, string slug) { TextRules.RequireSlug(slug); Slug = slug; Rename(name); }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = "";
    public string Slug { get; private set; }
    public Guid Version { get; private set; }
    public void Rename(string name) { Name = TextRules.RequireTrimmed(name, 80, "name"); Version = Guid.NewGuid(); }
}
