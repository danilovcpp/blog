using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Publishing;

[Trait("Category", "Publishing")]
public sealed class PublicationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
    internal static PublicationContent Content(string markdown = "text", string slug = "hello") => new(" Title ", slug, markdown, null, null, [], []);

    [Fact]
    public void LifecyclePreservesOwnerFirstDateAndSlugAndUsesCurrentVersionForNoOps()
    {
        var owner = Guid.NewGuid();
        var publication = Publication.Create(owner, Content(""), Now);
        Assert.Equal("Title", publication.Title);
        Assert.Null(publication.FirstPublishedAt);
        Assert.Throws<DomainError>(() => publication.Publish(Now));
        publication.Edit(Content(), Now);
        publication.Publish(Now);
        var version = publication.Version;
        publication.Publish(Now.AddMinutes(1));
        Assert.Equal(version, publication.Version);
        Assert.Equal(Now, publication.UpdatedAt);
        publication.Edit(Content("changed"), Now.AddMinutes(2));
        Assert.Equal(PublicationState.Published, publication.State);
        publication.Unpublish(Now.AddMinutes(3));
        version = publication.Version;
        publication.Unpublish(Now.AddMinutes(4));
        Assert.Equal(version, publication.Version);
        Assert.Throws<DomainError>(() => publication.Edit(Content(slug: "new"), Now));
        publication.Publish(Now.AddMinutes(5));
        Assert.Equal(Now, publication.FirstPublishedAt);
        Assert.Equal(owner, publication.AuthorId);
        publication.Delete(Now.AddMinutes(6));
        Assert.Throws<DomainError>(() => publication.Publish(Now));
        Assert.Throws<DomainError>(() => publication.Edit(Content(), Now));
        Assert.Throws<DomainError>(() => publication.Delete(Now));
    }

    [Fact]
    public void ContentChecksScalarBytesUrlsNullsAndClassificationBoundaries()
    {
        var valid = Content() with
        {
            Title = string.Concat(Enumerable.Repeat("😀", 200)),
            Markdown = new string('a', 1_048_576),
            Description = new string('a', 500),
            CoverUrl = "HTTPS://example.test/image",
            CategoryIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray(),
            TagIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray()
        };
        Publication.Create(Guid.NewGuid(), valid, Now);
        foreach (var invalid in new[] { valid with { Title = valid.Title + "x" }, valid with { Title = "\u0085\u3000" },
            valid with { Markdown = valid.Markdown + "é" }, valid with { Description = new string('a', 501) },
            valid with { CoverUrl = "http://example.test" }, valid with { CoverUrl = "https:///" }, valid with { CoverUrl = new string('a', 2049) },
            valid with { Slug = " Bad " }, valid with { Markdown = "\0" }, valid with { Title = "\ud800" },
            valid with { CategoryIds = [Guid.Empty, Guid.Empty] }, valid with { CategoryIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray() },
            valid with { TagIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray() } })
        { Assert.Throws<DomainError>(() => Publication.Create(Guid.NewGuid(), invalid, Now)); }
        var whitespace = Publication.Create(Guid.NewGuid(), Content(TextRules.WhiteSpace), Now);
        Assert.Throws<DomainError>(() => whitespace.Publish(Now));
        Assert.Equal("", Publication.Create(Guid.NewGuid(), Content() with { Description = "" }, Now).Description);
    }
}
