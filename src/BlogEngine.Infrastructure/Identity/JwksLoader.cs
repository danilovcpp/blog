using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace BlogEngine.Infrastructure.Identity;

public sealed class JwksLoader(HttpClient client, JwksSettings settings, TimeProvider time, CancellationToken stoppingToken)
{
    private readonly object sync = new();
    private JwksSnapshot? snapshot;
    private DateTimeOffset? lastAttempt;
    private Task<JwksSnapshot>? inFlight;
    private const int MaximumBytes = 256 * 1024;

    public async Task<JwksSnapshot> GetAsync(string? requiredKeyId, CancellationToken cancellationToken)
    {
        Task<JwksSnapshot> pending;
        TaskCompletionSource<JwksSnapshot>? start = null;
        lock (sync)
        {
            var now = time.GetUtcNow();
            if (snapshot?.IsFresh(now) == true && (requiredKeyId is null || snapshot.Keys.ContainsKey(requiredKeyId)))
            {
                return snapshot;
            }
            if (inFlight is { } active)
            {
                pending = active;
            }
            else if (lastAttempt is { } previous && now - previous < TimeSpan.FromSeconds(30))
            {
                if (snapshot?.IsFresh(now) == true) { return snapshot; }
                throw new IdentityKeysUnavailableException();
            }
            else
            {
                lastAttempt = now;
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = inFlight = start.Task;
            }
        }
        if (start is not null) { _ = FetchAndPublishAsync(start); }
        return await pending.WaitAsync(cancellationToken);
    }

    private async Task FetchAndPublishAsync(TaskCompletionSource<JwksSnapshot> completion)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var startedAt = time.GetUtcNow();
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.Url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) { throw new InvalidDataException(); }
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (content.Length + read > MaximumBytes) { throw new InvalidDataException(); }
                await content.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            var keys = ReadKeys(content.ToArray());
            timeout.Token.ThrowIfCancellationRequested();
            var freshness = response.Headers.CacheControl?.MaxAge ?? TimeSpan.FromSeconds(300);
            freshness -= response.Headers.Age ?? TimeSpan.Zero;
            freshness = TimeSpan.FromSeconds(Math.Clamp(freshness.TotalSeconds, 0, 300));
            if (response.Headers.CacheControl?.NoCache == true || response.Headers.CacheControl?.NoStore == true)
            {
                freshness = TimeSpan.Zero;
            }
            // Receiving a slow response must not extend the freshness advertised at its beginning.
            var updated = new JwksSnapshot(keys, startedAt + freshness);
            if (!updated.IsFresh(time.GetUtcNow())) { throw new InvalidDataException(); }
            lock (sync)
            {
                snapshot = updated;
                inFlight = null;
                completion.TrySetResult(updated);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException
            or CryptographicException or ArgumentException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            lock (sync)
            {
                inFlight = null;
                completion.TrySetException(new IdentityKeysUnavailableException());
            }
        }
    }

    private static ImmutableDictionary<string, SecurityKey> ReadKeys(byte[] content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
        RequireUniqueProperties(document.RootElement);
        var array = document.RootElement.GetProperty("keys");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32) { throw new InvalidDataException(); }
        var keys = ImmutableDictionary.CreateBuilder<string, SecurityKey>(StringComparer.Ordinal);
        foreach (var value in array.EnumerateArray())
        {
            RequireUniqueProperties(value);
            if (String(value, "kty") != "RSA" || String(value, "use") != "sig" || String(value, "alg") != "RS256")
            {
                throw new InvalidDataException();
            }
            foreach (var name in new[] { "d", "p", "q", "dp", "dq", "qi", "oth" })
            {
                if (value.TryGetProperty(name, out _)) { throw new InvalidDataException(); }
            }
            if (value.TryGetProperty("key_ops", out var operations) &&
                (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() != 1 || operations[0].GetString() != "verify"))
            {
                throw new InvalidDataException();
            }
            var kid = String(value, "kid");
            var parameters = new RSAParameters
            {
                Modulus = Base64UrlEncoder.DecodeBytes(String(value, "n")),
                Exponent = Base64UrlEncoder.DecodeBytes(String(value, "e")),
            };
            using var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            if (rsa.KeySize < 2048 || !keys.TryAdd(kid, new RsaSecurityKey(parameters) { KeyId = kid }))
            {
                throw new InvalidDataException();
            }
        }
        return keys.ToImmutable();
    }

    internal static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) { throw new InvalidDataException(); }
        }
    }

    internal static string String(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
        {
            throw new InvalidDataException();
        }
        return field.GetString()!;
    }
}
