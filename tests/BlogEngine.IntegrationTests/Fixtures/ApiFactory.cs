using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BlogEngine.IntegrationTests.Fixtures;

public sealed class ApiFactory(string connectionString, JwksFixture identity) : WebApplicationFactory<Program>
{
    public AdjustableTimeProvider Time => identity.Time;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
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
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }
}
