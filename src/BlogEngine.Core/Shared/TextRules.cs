using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace BlogEngine.Core.Shared;

public static partial class TextRules
{
    public const int MaximumMarkdownBytes = 1_048_576;
    public const string WhiteSpace = "\u0009\u000a\u000b\u000c\u000d\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";
    private static readonly char[] TrimCharacters = WhiteSpace.ToCharArray();

    public static string TrimWhiteSpace(string value) => value.Trim(TrimCharacters);

    public static int ScalarLength(string value, string field = "value")
    {
        var remaining = value.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done || rune.Value == 0)
            {
                throw DomainError.Validation(field);
            }
            count++;
            remaining = remaining[consumed..];
        }
        return count;
    }

    public static int Utf8Length(string value, string field = "value")
    {
        ScalarLength(value, field);
        return Encoding.UTF8.GetByteCount(value);
    }

    public static void RequireMarkdown(string value, string field = "markdown")
    {
        if (Utf8Length(value, field) > MaximumMarkdownBytes) { throw DomainError.Validation(field); }
    }

    public static string RequireTrimmed(string value, int maximum, string field, int minimum = 1)
    {
        var result = TrimWhiteSpace(value);
        var length = ScalarLength(result, field);
        if (length < minimum || length > maximum)
        {
            throw DomainError.Validation(field);
        }
        return result;
    }

    public static void RequireSlug(string value, string field = "slug")
    {
        if (ScalarLength(value, field) is < 1 or > 120 || !SlugPattern().IsMatch(value))
        {
            throw DomainError.Validation(field);
        }
    }

    [GeneratedRegex("\\A[a-z0-9]+(-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SlugPattern();
}
