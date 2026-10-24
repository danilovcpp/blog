using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BlogEngine.Infrastructure.Identity;

public sealed class BearerKeyValidation(JwksLoader loader, TimeProvider time)
{
    public const int MaximumTokenBytes = 16 * 1024;

    public async Task<TokenValidationResult> ValidateAsync(string token, TokenValidationParameters parameters, CancellationToken cancellationToken)
    {
        try
        {
            var kid = CheckEnvelope(token, parameters);
            var snapshot = await loader.GetAsync(kid, cancellationToken);
            if (!snapshot.IsFresh(time.GetUtcNow()) || !snapshot.Keys.TryGetValue(kid, out var key))
            {
                return Invalid();
            }
            // Each request gets its own parameters; no synchronous network key resolver or shared option mutation.
            var local = parameters.Clone();
            local.IssuerSigningKey = key;
            local.IssuerSigningKeys = null;
            local.LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return notBefore.HasValue && expires.HasValue && notBefore <= expires
                    && notBefore <= now.AddSeconds(30) && expires >= now.AddSeconds(-30);
            };
            var handler = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = MaximumTokenBytes };
            var result = await handler.ValidateTokenAsync(token, local);
            // Never pass a library exception (which may contain token data) to framework logging.
            return result.IsValid ? result : Invalid();
        }
        catch (IdentityKeysUnavailableException exception)
        {
            return new TokenValidationResult { IsValid = false, Exception = exception };
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Invalid();
        }
    }

    private string CheckEnvelope(string token, TokenValidationParameters parameters)
    {
        if (Encoding.UTF8.GetByteCount(token) > MaximumTokenBytes) { throw new InvalidDataException(); }
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) { throw new InvalidDataException(); }
        using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]));
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
        JwksLoader.RequireUniqueProperties(header.RootElement);
        JwksLoader.RequireUniqueProperties(payload.RootElement);
        var h = header.RootElement;
        var p = payload.RootElement;
        if (JwksLoader.String(h, "alg") != "RS256" || JwksLoader.String(h, "typ") != "JWT"
            || JwksLoader.String(p, "iss") != parameters.ValidIssuer || JwksLoader.String(p, "aud") != parameters.ValidAudience
            || JwksLoader.String(p, "token_use") != "access") { throw new InvalidDataException(); }
        if (h.TryGetProperty("crit", out _)) { throw new InvalidDataException(); }
        foreach (var name in new[] { "sub", "sid" })
        {
            if (!Guid.TryParseExact(JwksLoader.String(p, name), "D", out _)) { throw new InvalidDataException(); }
        }
        JwksLoader.String(p, "client_id");
        JwksLoader.String(p, "jti");
        var issued = NumericDate(p, "iat");
        var notBefore = NumericDate(p, "nbf");
        var expires = NumericDate(p, "exp");
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (issued > now + 30 || notBefore > now + 30 || expires < now - 30 || issued > expires || notBefore > expires)
        {
            throw new InvalidDataException();
        }
        return JwksLoader.String(h, "kid");
    }

    private static long NumericDate(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)
            || number is < -62135596800 or > 253402300799) { throw new InvalidDataException(); }
        return number;
    }

    private static TokenValidationResult Invalid() => new() { IsValid = false, Exception = new SecurityTokenValidationException("Invalid access token.") };
}
