using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BlogEngine.IntegrationTests.Fixtures;

public sealed class JwksFixture : IAsyncLifetime, IDisposable
{
    private readonly object sync = new();
    private RSA signingRsa = RSA.Create(2048);
    private string keyId = Guid.NewGuid().ToString("N");
    private WebApplication? server;
    private Uri? jwksUrl;
    private string response = string.Empty;
    private int responseStatus = StatusCodes.Status200OK;
    private int requestCount;

    public JwksFixture()
    {
        ResetResponse();
    }

    public AdjustableTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public string Issuer { get; } = "urn:blog-engine:test:" + Guid.NewGuid().ToString("N");
    public string Audience => "blog-engine-tests";
    public Uri JwksUrl => jwksUrl ?? throw new InvalidOperationException("Initialize the JWKS fixture first.");
    public int RequestCount => Volatile.Read(ref requestCount);

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        server = builder.Build();
        server.MapGet("/.well-known/jwks.json", async context =>
        {
            Interlocked.Increment(ref requestCount);
            string body;
            lock (sync)
            {
                body = response;
                context.Response.StatusCode = responseStatus;
            }

            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "public, max-age=300";
            await context.Response.WriteAsync(body, context.RequestAborted);
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await server.StartAsync(timeout.Token);
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        jwksUrl = new Uri(new Uri(address), "/.well-known/jwks.json");
    }

    public string CreateToken(Guid? subject = null, string? issuer = null, string? audience = null)
    {
        lock (sync)
        {
            var now = Time.GetUtcNow().UtcDateTime;
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer ?? Issuer,
                Audience = audience ?? Audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(15),
                TokenType = "JWT",
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = (subject ?? Guid.NewGuid()).ToString(),
                    ["sid"] = Guid.NewGuid().ToString(),
                    ["client_id"] = "fixture",
                    ["jti"] = Guid.NewGuid().ToString(),
                    ["token_use"] = "access",
                },
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(signingRsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256),
            });
        }
    }

    public void RotateKey()
    {
        lock (sync)
        {
            signingRsa.Dispose();
            signingRsa = RSA.Create(2048);
            keyId = Guid.NewGuid().ToString("N");
            ResetResponse();
        }
    }

    public void SetResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        lock (sync)
        {
            response = body;
            responseStatus = (int)status;
        }
    }

    public void ResetResponse()
    {
        lock (sync)
        {
            var parameters = signingRsa.ExportParameters(false);
            response = JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = "RS256",
                        kid = keyId,
                        n = Base64UrlEncoder.Encode(parameters.Modulus!),
                        e = Base64UrlEncoder.Encode(parameters.Exponent!),
                    },
                },
            });
            responseStatus = StatusCodes.Status200OK;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (server is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StopAsync(timeout.Token);
            await server.DisposeAsync();
        }

        Dispose();
    }

    public void Dispose() => signingRsa.Dispose();
}
