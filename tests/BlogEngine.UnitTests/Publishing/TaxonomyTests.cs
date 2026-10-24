using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Publishing;

[Trait("Category", "Publishing")]
public sealed class TaxonomyTests
{
    [Fact]
    public void NamesAreTrimmedByScalarsAndSlugsRemainUnchanged()
    {
        var category = new Category(" Name\u3000", "same");
        var tag = new Tag("Name", "same");
        Assert.Equal(category.Slug, tag.Slug);
        Assert.Equal("Name", category.Name);
        var version = category.Version;
        category.Rename(string.Concat(Enumerable.Repeat("😀", 80)));
        Assert.NotEqual(version, category.Version);
        Assert.Equal("same", category.Slug);
        Assert.Throws<DomainError>(() => category.Rename(new string('a', 81)));
        Assert.Throws<DomainError>(() => new Tag("\u0085", "valid"));
        Assert.Throws<DomainError>(() => new Category("valid", "Bad"));
        Assert.Throws<DomainError>(() => new Tag("valid", "slug\n"));
    }
}
