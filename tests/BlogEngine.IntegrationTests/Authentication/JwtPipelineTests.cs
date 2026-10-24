using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.IntegrationTests.Fixtures;

namespace BlogEngine.IntegrationTests.Authentication;

[Trait("Category", "Authentication")]
public sealed class JwtPipelineTests
{
    [Fact]
    public async Task ExactIssuerAudienceSignatureAndAllScalarClaimsAreRequired()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        await AssertToken(client, identity.CreateToken(), HttpStatusCode.OK);
        await AssertToken(client, identity.CreateToken(issuer: identity.Issuer + "/"), HttpStatusCode.Unauthorized);
        await AssertToken(client, identity.CreateToken(audience: identity.Audience + "/"), HttpStatusCode.Unauthorized);
        var required = new[] { "sub", "sid", "client_id", "jti", "token_use", "iat", "nbf", "exp", "iss", "aud" };
        foreach (var claim in required)
        {
            await AssertToken(client, identity.CreateToken(customize: claims => claims.Remove(claim)), HttpStatusCode.Unauthorized);
            await AssertToken(client, identity.CreateToken(customize: claims => claims[claim] = new[] { "wrong" }), HttpStatusCode.Unauthorized);
            await AssertToken(client, identity.CreateToken(customize: claims => claims[claim] = true), HttpStatusCode.Unauthorized);
        }
        foreach (var claim in new[] { "sub", "sid" })
        {
            await AssertToken(client, identity.CreateToken(customize: claims => claims[claim] = "invalid-uuid"), HttpStatusCode.Unauthorized);
        }
        foreach (var claim in new[] { "client_id", "jti" })
        {
            await AssertToken(client, identity.CreateToken(customize: claims => claims[claim] = ""), HttpStatusCode.Unauthorized);
        }
        await AssertToken(client, identity.CreateToken(customize: claims => claims["token_use"] = "refresh"), HttpStatusCode.Unauthorized);
        await AssertToken(client, identity.CreateToken(customize: claims => claims["extra"] = new string('x', 17000)), HttpStatusCode.Unauthorized);
        Assert.Equal(1, identity.RequestCount);
    }

    [Fact]
    public async Task DuplicatesHeaderAlgorithmSignatureAndTimeBoundariesAreRejected()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        string payload = "";
        identity.CreateToken(customize: claims => payload = JsonSerializer.Serialize(claims));
        foreach (var claim in new[] { "sub", "sid", "client_id", "jti", "token_use", "iat", "nbf", "exp", "iss", "aud" })
        {
            using var doc = JsonDocument.Parse(payload);
            var duplicate = payload[..^1] + "," + JsonSerializer.Serialize(claim) + ":" + doc.RootElement.GetProperty(claim).GetRawText() + "}";
            await AssertToken(client, identity.CreateRawToken(duplicate), HttpStatusCode.Unauthorized);
        }
        foreach (var algorithm in new[] { "none", "HS256", "RS512" })
        {
            await AssertToken(client, identity.CreateRawToken(payload, header => header["alg"] = algorithm), HttpStatusCode.Unauthorized);
        }
        await AssertToken(client, identity.CreateRawToken(payload, header => header["typ"] = "refresh"), HttpStatusCode.Unauthorized);
        var now = identity.Time.GetUtcNow().ToUnixTimeSeconds();
        await AssertToken(client, identity.CreateToken(customize: c => c["iat"] = now + 31), HttpStatusCode.Unauthorized);
        await AssertToken(client, identity.CreateToken(customize: c => c["nbf"] = now + 31), HttpStatusCode.Unauthorized);
        await AssertToken(client, identity.CreateToken(customize: c => { c["nbf"] = now - 100; c["iat"] = now - 100; c["exp"] = now - 31; }), HttpStatusCode.Unauthorized);
        await AssertToken(client, identity.CreateToken(customize: c => c["iat"] = now + 30), HttpStatusCode.OK);
        await AssertToken(client, identity.CreateToken(customize: c => c["nbf"] = now + 30), HttpStatusCode.OK);
        await AssertToken(client, identity.CreateToken(customize: c => c["exp"] = now + 86400), HttpStatusCode.OK);
        var token = identity.CreateToken();
        var segments = token.Split('.');
        var signature = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(segments[2]);
        signature[0] ^= 1;
        await AssertToken(client, segments[0] + "." + segments[1] + "." + Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(signature), HttpStatusCode.Unauthorized);
        await AssertToken(client, identity.CreateRawToken(payload, header => { header["jku"] = "http://127.0.0.1:1/evil"; header["x5u"] = "http://127.0.0.1:1/evil"; }), HttpStatusCode.OK);
    }

    internal static async Task AssertToken(HttpClient client, string token, HttpStatusCode expected, string path = "/__foundation/token")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(expected == HttpStatusCode.Unauthorized ? "invalid_token" : "identity_keys_unavailable", error.GetProperty("code").GetString());
            Assert.True(error.TryGetProperty("traceId", out _));
            if (expected == HttpStatusCode.Unauthorized) { Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme); }
        }
    }
}
