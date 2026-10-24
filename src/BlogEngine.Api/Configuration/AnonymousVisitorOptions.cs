using BlogEngine.Infrastructure.Visitors;
using Microsoft.Extensions.Options;

namespace BlogEngine.Api.Configuration;

public sealed class AnonymousVisitorOptions
{
    public string ActiveKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AnonymousVisitorOptionsValidator : IValidateOptions<AnonymousVisitorOptions>
{
    public ValidateOptionsResult Validate(string? name, AnonymousVisitorOptions options)
    {
        try { _ = new AnonymousVisitorTokenService(options.ActiveKeyId, options.Keys); return ValidateOptionsResult.Success; }
        catch (ArgumentException) { return ValidateOptionsResult.Fail("AnonymousVisitors requires valid key IDs, 32-byte base64 secrets and an existing ActiveKeyId."); }
    }
}
