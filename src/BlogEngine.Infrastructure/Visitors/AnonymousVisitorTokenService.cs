using System.Security.Cryptography;
using System.Text;

namespace BlogEngine.Infrastructure.Visitors;

public sealed class AnonymousVisitorTokenService
{
    private readonly string activeKeyId;
    private readonly Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);
    public AnonymousVisitorTokenService(string activeKeyId, IReadOnlyDictionary<string, string> keys)
    {
        const string error = "Anonymous visitor key ring requires valid IDs, 32-byte base64 secrets and an existing active key.";
        foreach (var (id, encoded) in keys)
        {
            if (id.Length is < 1 or > 32 || id.Any(c => !IsBase64Url(c))) { throw new ArgumentException(error); }
            byte[] secret;
            try { secret = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new ArgumentException(error); }
            if (secret.Length != 32) { throw new ArgumentException(error); }
            this.keys.Add(id, secret);
        }
        if (string.IsNullOrEmpty(activeKeyId) || !this.keys.ContainsKey(activeKeyId)) { throw new ArgumentException(error); }
        this.activeKeyId = activeKeyId;
    }

    public string Issue()
    {
        var prefix = $"v1.{activeKeyId}.{Encode(RandomNumberGenerator.GetBytes(32))}";
        return prefix + "." + Encode(HMACSHA256.HashData(keys[activeKeyId], Encoding.UTF8.GetBytes(prefix)));
    }

    public bool TryRead(string? token, out byte[] payload)
    {
        payload = [];
        if (token is null || token.Length > 123) { return false; }
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !keys.TryGetValue(parts[1], out var key)
            || !TryDecode(parts[2], out var random) || !TryDecode(parts[3], out var mac)) { return false; }
        var prefix = token[..token.LastIndexOf('.')];
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(prefix)), mac)) { return false; }
        payload = random; return true;
    }

    private static bool IsBase64Url(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length != 43 || value.Any(c => !IsBase64Url(c))) { return false; }
        try { bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "="); }
        catch (FormatException) { return false; }
        // Reject alternate encodings with nonzero unused bits, retaining one canonical representation.
        return bytes.Length == 32 && Encode(bytes) == value;
    }
}
