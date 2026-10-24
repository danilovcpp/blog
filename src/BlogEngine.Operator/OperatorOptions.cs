using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.Operator;

public sealed class OperatorOptions
{
    public string Issuer { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OperatorOptions>().Bind(configuration.GetSection("Operator")).Configure(options =>
        {
            options.Issuer = configuration["Identity:Issuer"] ?? string.Empty;
            options.ConnectionString = configuration.GetConnectionString("Blog") ?? string.Empty;
        }).Validate(options => !string.IsNullOrWhiteSpace(options.Issuer) && options.Issuer == options.Issuer.Trim(), "Identity:Issuer must be explicitly configured.")
            .Validate(options => HasConnection(options.ConnectionString), "ConnectionStrings:Blog must be configured with separate operator credentials.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 300, "Operator:TimeoutSeconds must be between 1 and 300.")
            .ValidateOnStart();
    }

    private static bool HasConnection(string connection)
    {
        try { return !string.IsNullOrWhiteSpace(connection) && !string.IsNullOrWhiteSpace(new NpgsqlConnectionStringBuilder(connection).Host); }
        catch (ArgumentException) { return false; }
    }
}
