using Microsoft.Extensions.Options;

namespace BlogEngine.Api.Configuration;

public sealed class IdentityOptions
{
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string JwksUrl { get; set; } = string.Empty;
    public bool AllowLocalHttp { get; set; }
}

public sealed class IdentityOptionsValidator(IHostEnvironment environment) : IValidateOptions<IdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer) || options.Issuer != options.Issuer.Trim()
            || string.IsNullOrWhiteSpace(options.Audience) || options.Audience != options.Audience.Trim())
        {
            return ValidateOptionsResult.Fail("Identity:Issuer and Identity:Audience must be explicitly configured and exact.");
        }
        if (!Uri.TryCreate(options.JwksUrl, UriKind.Absolute, out var url) || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Fragment) || (url.Scheme != Uri.UriSchemeHttps &&
            !(environment.IsDevelopment() && options.AllowLocalHttp && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)))
        {
            return ValidateOptionsResult.Fail("Identity:JwksUrl requires HTTPS; explicitly enabled loopback HTTP is allowed only in Development.");
        }
        return ValidateOptionsResult.Success;
    }
}
