using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BlogEngine.IntegrationTests.Media;

public sealed class MediaOptionsPipelineTests
{
    [Fact]
    public async Task ProductionRejectsLocalHttpEvenWithExplicitOptIn()
    {
        await using var identity = new JwksFixture(); await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        await using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Identity:JwksUrl"] = "https://identity.fixture.test/jwks" })));
        var error = Assert.Throws<OptionsValidationException>(() => { using var client = production.CreateClient(); });
        Assert.Contains("Media local HTTP", error.Message, StringComparison.Ordinal);
    }
}
