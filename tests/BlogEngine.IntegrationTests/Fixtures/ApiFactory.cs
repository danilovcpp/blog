using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlogEngine.IntegrationTests.Fixtures;

public sealed class ApiFactory(string connectionString, JwksFixture identity) : WebApplicationFactory<Program>
{
    public const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1;Command Timeout=1";
    public AdjustableTimeProvider Time => identity.Time;
    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Blog"] = connectionString,
                ["Identity:Issuer"] = identity.Issuer,
                ["Identity:Audience"] = identity.Audience,
                ["Identity:JwksUrl"] = identity.JwksUrl.AbsoluteUri,
                ["Identity:AllowLocalHttp"] = "true",
                ["AllowedHosts"] = "localhost;127.0.0.1",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IStartupFilter, FoundationProbes>();
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }
}
