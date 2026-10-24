using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlogEngine.Performance;

// Isolated benchmark infrastructure. Neither private keys nor JWTs leave process memory.
public sealed class BenchmarkIdentityFixture : IAsyncDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly string kid = Guid.NewGuid().ToString("N");
    private readonly string issuer;
    private readonly string audience;
    private WebApplication? host;
    private readonly string[] tokens;
    public Uri? LocalJwksUrl { get; private set; }
    public BenchmarkIdentityFixture(string issuer, string audience)
    {
        this.issuer = issuer; this.audience = audience;
        tokens = Enumerable.Range(0, 10000).Select(CreateToken).ToArray();
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private string CreateToken(int user)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer,
            aud = audience,
            sub = SeedDataset.Id("subject", user),
            sid = Guid.NewGuid(),
            client_id = "isolated-sc005-fixture",
            jti = Guid.NewGuid(),
            token_use = "access",
            iat = now,
            nbf = now,
            exp = now + 3600,
        }));
        var input = header + "." + payload;
        return input + "." + Encode(rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    public string Token(int user) => tokens[user];
    public async Task StartAsync(string listenUrl, Uri publicJwksUrl, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls(listenUrl);
        host = builder.Build(); var key = rsa.ExportParameters(false);
        var document = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid, n = Encode(key.Modulus!), e = Encode(key.Exponent!) } } });
        host.MapGet(publicJwksUrl.AbsolutePath, (HttpContext context) =>
        { context.Response.Headers.CacheControl = "public, max-age=300"; return Results.Text(document, "application/json"); });
        await host.StartAsync(ct);
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        LocalJwksUrl = new Uri(new Uri(address), publicJwksUrl.AbsolutePath);
    }
    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await host.StopAsync(stop.Token); await host.DisposeAsync(); }
        rsa.Dispose(); Array.Clear(tokens);
    }
}
