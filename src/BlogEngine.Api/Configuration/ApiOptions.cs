using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.Api.Configuration;

public sealed class ApiOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class ApiOptionsValidator : IValidateOptions<ApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiOptions options)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString) || string.IsNullOrWhiteSpace(new NpgsqlConnectionStringBuilder(options.ConnectionString).Host))
            {
                return ValidateOptionsResult.Fail("ConnectionStrings:Blog must be configured.");
            }
        }
        catch (ArgumentException) { return ValidateOptionsResult.Fail("ConnectionStrings:Blog is invalid."); }
        if (options.AllowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || origin != uri.GetLeftPart(UriPartial.Authority)
            || !string.IsNullOrEmpty(uri.UserInfo)))
        {
            return ValidateOptionsResult.Fail("Api:AllowedOrigins must contain explicit HTTP(S) origins.");
        }
        return ValidateOptionsResult.Success;
    }
}
