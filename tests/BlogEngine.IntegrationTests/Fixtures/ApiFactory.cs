using System.Security.Cryptography;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlogEngine.IntegrationTests.Fixtures;

public sealed class ApiFactory(string connectionString, JwksFixture identity, DbCommandInterceptor? commandInterceptor = null) : WebApplicationFactory<Program>
{
    public const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1;Command Timeout=1";
    public AdjustableTimeProvider Time => identity.Time;
    public CapturedLogs Logs { get; } = new();
    public DatabaseClock? ViewClock { get; init; }
    public IReadOnlyDictionary<string, string> VisitorKeys { get; init; } = new Dictionary<string, string>
    { ["fixture"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Blog"] = connectionString,
                ["Identity:Issuer"] = identity.Issuer,
                ["Identity:Audience"] = identity.Audience,
                ["Identity:JwksUrl"] = identity.JwksUrl.AbsoluteUri,
                ["Identity:AllowLocalHttp"] = "true",
                ["AllowedHosts"] = "localhost;127.0.0.1",
                ["AnonymousVisitors:ActiveKeyId"] = VisitorKeys.Keys.First(),
            };
            foreach (var (id, secret) in VisitorKeys) { values["AnonymousVisitors:Keys:" + id] = secret; }
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(services =>
        {
            if (commandInterceptor is not null) { services.AddDbContext<BlogDbContext>(options => options.AddInterceptors(commandInterceptor)); }
            services.AddSingleton<TimeProvider>(Time);
            if (ViewClock is not null) { services.AddSingleton(ViewClock); }
            services.AddSingleton<IStartupFilter, FoundationProbes>();
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }
}
