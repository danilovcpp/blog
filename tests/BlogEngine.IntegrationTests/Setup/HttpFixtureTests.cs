using System.Net;
using System.Net.Http.Headers;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace BlogEngine.IntegrationTests.Setup;

[Trait("Category", "Setup")]
public sealed class HttpFixtureTests
{
    [Fact]
    public async Task FixtureJwksAndTokensExerciseTheRealBearerMiddlewareInApiHost()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        using var jwksClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var jwks = new JsonWebKeySet(await jwksClient.GetStringAsync(identity.JwksUrl, cancellationToken));
        Assert.Equal(1, identity.RequestCount);
        Assert.Single(jwks.Keys);
        Assert.Null(jwks.Keys[0].D);

        // No production connection or Identity is contacted by this HTTP-only smoke test.
        await using var factory = new ApiFactory(string.Empty, identity);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = identity.Issuer,
                    ValidAudience = identity.Audience,
                    IssuerSigningKeys = jwks.GetSigningKeys(),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    RequireSignedTokens = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                };
            });
            services.AddSingleton<IStartupFilter, BearerProbe>();
        }));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identity.CreateToken());
        using var accepted = await client.GetAsync("/__setup/bearer", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var otherKey = new JwksFixture();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherKey.CreateToken(issuer: identity.Issuer));
        using var rejected = await client.GetAsync("/__setup/bearer", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        using var missing = await client.GetAsync("/__setup/bearer", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
    }

    [Fact]
    public async Task JwksCanRotateAndReturnControlledFailures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var initial = new JsonWebKeySet(await client.GetStringAsync(identity.JwksUrl, cancellationToken));
        identity.RotateKey();
        var rotated = new JsonWebKeySet(await client.GetStringAsync(identity.JwksUrl, cancellationToken));
        Assert.NotEqual(initial.Keys.Single().Kid, rotated.Keys.Single().Kid);
        identity.SetResponse("unavailable", HttpStatusCode.ServiceUnavailable);
        using var failure = await client.GetAsync(identity.JwksUrl, cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal(3, identity.RequestCount);
    }

    private sealed class BearerProbe : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseAuthentication();
            app.Map("/__setup/bearer", probe => probe.Run(async context =>
            {
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsync("verified", context.RequestAborted);
                }
                else
                {
                    await context.ChallengeAsync();
                }
            }));
            next(app);
        };
    }
}
