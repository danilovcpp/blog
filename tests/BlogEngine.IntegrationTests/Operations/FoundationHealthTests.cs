using System.Net;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.IntegrationTests.Operations;

[Trait("Category", "Operations")]
public sealed class FoundationHealthTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ReadinessRecoversUsingTheSharedBoundedLoaderWhileLivenessIsIndependent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        identity.SetResponse("down", HttpStatusCode.ServiceUnavailable);
        await using var factory = new ApiFactory(database.ConnectionString, identity);
        using var client = factory.CreateClient();
        using var live = await client.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(0, identity.RequestCount);
        using var unavailable = await client.GetAsync("/health/ready", ct);
        await InfrastructureContractTests.AssertProblem(unavailable, 503, "identity_keys_unavailable");
        identity.ResetResponse();
        using var cooldown = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, cooldown.StatusCode);
        Assert.Equal(1, identity.RequestCount);
        identity.Time.Advance(TimeSpan.FromSeconds(30));
        using var ready = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        identity.SetResponse("down", HttpStatusCode.ServiceUnavailable);
        using var cached = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.Equal(2, identity.RequestCount);
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" }.ConnectionString))
        {
            await admin.OpenAsync(ct);
            var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database;
            await using var disable = new NpgsqlCommand($"ALTER DATABASE \"{databaseName}\" ALLOW_CONNECTIONS false", admin);
            await disable.ExecuteNonQueryAsync(ct);
            try
            {
                using var down = await client.GetAsync("/health/ready", ct);
                await InfrastructureContractTests.AssertProblem(down, 503, "dependency_unavailable");
            }
            finally
            {
                await using var enable = new NpgsqlCommand($"ALTER DATABASE \"{databaseName}\" ALLOW_CONNECTIONS true", admin);
                await enable.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        using var recovered = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, identity.RequestCount);
        identity.Time.Advance(TimeSpan.FromSeconds(300));
        using var expired = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, expired.StatusCode);
    }

    [Fact]
    public async Task DatabaseOutageDoesNotChangeLivenessAndProducesSafe503()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        await InfrastructureContractTests.AssertProblem(ready, 503, "dependency_unavailable");
        client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken());
        using var actor = await client.GetAsync("/__foundation/actor", TestContext.Current.CancellationToken);
        await InfrastructureContractTests.AssertProblem(actor, 503, "dependency_unavailable");
    }

    [Theory]
    [InlineData("Identity:Issuer", "")]
    [InlineData("Identity:Audience", "")]
    [InlineData("Identity:JwksUrl", "")]
    [InlineData("ConnectionStrings:Blog", "")]
    [InlineData("Identity:AllowLocalHttp", "false")]
    [InlineData("Identity:JwksUrl", "http://identity.example/keys")]
    [InlineData("Api:AllowedOrigins:0", "*")]
    public async Task InvalidConfigurationFailsStartupWithoutContactingDependencies(string key, string value)
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        await using var invalid = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.ThrowsAny<OptionsValidationException>(() => invalid.CreateClient());
        Assert.Equal(0, identity.RequestCount);
    }

    [Fact]
    public async Task ProductionRejectsLocalHttpAndDoesNotExposeDevelopmentDocumentation()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        await using var insecure = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        Assert.ThrowsAny<OptionsValidationException>(() => insecure.CreateClient());
        await using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:JwksUrl"] = "https://keys.invalid/jwks.json",
            })));
        using var client = production.CreateClient();
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        foreach (var path in new[] { "/openapi/v1.json", "/swagger/index.html" })
        {
            using var hidden = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        Assert.Equal(0, identity.RequestCount);
    }
}
