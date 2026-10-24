using BlogEngine.Core.ReadModels;
using BlogEngine.Core.Shared;

namespace BlogEngine.UnitTests.Shared;

public sealed class InputRulesTests
{
    [Fact]
    public void ScalarLengthAndUtf8AreIndependentLimits()
    {
        Assert.Equal(2, TextRules.ScalarLength("a😀"));
        Assert.Equal(5, TextRules.Utf8Length("a😀"));
        Assert.Throws<DomainError>(() => TextRules.ScalarLength("a\0"));
        Assert.Throws<DomainError>(() => TextRules.ScalarLength("\ud800"));
        Assert.Equal("title", TextRules.TrimWhiteSpace(TextRules.WhiteSpace + "title" + TextRules.WhiteSpace));
        Assert.Equal("\u200btitle\ufeff", TextRules.TrimWhiteSpace("\u200btitle\ufeff"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("abc-012")]
    public void StrictSlugAcceptsCanonicalInput(string value) => TextRules.RequireSlug(value);

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData(" a")]
    [InlineData("a ")]
    [InlineData("a--b")]
    [InlineData("-a")]
    [InlineData("a\n")]
    [InlineData("я")]
    public void SlugNeverNormalizesInvalidInput(string value) =>
        Assert.Throws<DomainError>(() => TextRules.RequireSlug(value));

    [Fact]
    public void PagesUseCheckedInt64OffsetsAndBoundedLookahead()
    {
        Assert.Equal(0, new PageRequest().Offset);
        Assert.Equal(20, new PageRequest().PageSize);
        var far = new PageRequest(int.MaxValue, 100);
        Assert.Equal(214748364600L, far.Offset);
        Assert.Equal(101, far.FetchSize);
        var empty = PageResult<int>.FromLookahead([], far);
        Assert.Empty(empty.Items);
        Assert.False(empty.HasNextPage);
        Assert.Equal(int.MaxValue, empty.Page);
        var bounded = PageResult<int>.FromLookahead(Enumerable.Range(1, 21).ToArray(), new PageRequest());
        Assert.Equal(20, bounded.Items.Count);
        Assert.True(bounded.HasNextPage);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageResult<int>.FromLookahead(new int[102], far));
        Assert.Throws<DomainError>(() => new PageRequest(0));
        Assert.Throws<DomainError>(() => new PageRequest(1, 101));
        Assert.Throws<DomainError>(() => TextRules.RequireSlug(new string('a', 121)));
    }

    [Fact]
    public void MarkdownIsMeasuredAfterJsonDecodingWithoutTrimming()
    {
        TextRules.RequireMarkdown(new string(' ', TextRules.MaximumMarkdownBytes));
        TextRules.RequireMarkdown(string.Concat(Enumerable.Repeat("😀", TextRules.MaximumMarkdownBytes / 4)));
        Assert.Throws<DomainError>(() => TextRules.RequireMarkdown(new string('a', TextRules.MaximumMarkdownBytes + 1)));
        Assert.Throws<DomainError>(() => TextRules.RequireMarkdown("\0"));
        const string expected = "\u0009\u000a\u000b\u000c\u000d\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";
        Assert.Equal(expected, TextRules.WhiteSpace);
    }
}
