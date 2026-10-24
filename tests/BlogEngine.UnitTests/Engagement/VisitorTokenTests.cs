using System.Security.Cryptography;
using System.Text;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Engagement.Contracts;
using BlogEngine.Infrastructure.Visitors;

namespace BlogEngine.UnitTests.Engagement;

public sealed class VisitorTokenTests
{
    private static Dictionary<string, string> Keys() => new(StringComparer.Ordinal)
    {
        ["old"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["new"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    };

    [Fact]
    public void IssuanceUsesCanonicalRandom256AndAuthenticatedMac()
    {
        var keys = Keys(); var service = new AnonymousVisitorTokenService("old", keys);
        var tokens = Enumerable.Range(0, 100).Select(_ => service.Issue()).ToArray();
        Assert.Equal(100, tokens.Distinct().Count());
        foreach (var token in tokens)
        {
            Assert.Equal(94, token.Length); Assert.Matches(@"^v1\.old\.[A-Za-z0-9_-]{43}\.[A-Za-z0-9_-]{43}$", token);
            Assert.True(service.TryRead(token, out var payload)); Assert.Equal(32, payload.Length);
            var parts = token.Split('.');
            var expected = HMACSHA256.HashData(Convert.FromBase64String(keys["old"]), Encoding.UTF8.GetBytes(string.Join('.', parts[..3])));
            Assert.Equal(Base64Url(expected), parts[3]);
        }
    }

    [Fact]
    public void InvalidTokensAreReplacedAndRotationPreservesStablePayloadIdentity()
    {
        var keys = Keys(); var old = new AnonymousVisitorTokenService("old", keys); var token = old.Issue();
        var rotated = new AnonymousVisitorTokenService("new", keys);
        Assert.True(rotated.TryRead(token, out var payload));
        var forged = token.Split('.'); forged[3] = (forged[3][0] == 'A' ? "B" : "A") + forged[3][1..];
        Assert.False(rotated.TryRead(string.Join('.', forged), out _));
        var nonCanonical = token[..^1] + "B"; Assert.False(rotated.TryRead(nonCanonical, out _));
        var prefix = "v1.new." + Base64Url(payload);
        var resigned = prefix + "." + Base64Url(HMACSHA256.HashData(Convert.FromBase64String(keys["new"]), Encoding.UTF8.GetBytes(prefix)));
        var resolver = new VisitorIdentityResolver(rotated);
        var first = resolver.Resolve(null, token); var next = resolver.Resolve(null, resigned);
        Assert.Equal(first.Identity.Key.ToArray(), next.Identity.Key.ToArray()); Assert.Equal(token, first.AnonymousVisitorId);
        foreach (var invalid in new string?[] { null, "", "forged", new('x', 257), token + "=", token[..^1] + "!", token.Replace("old", "unknown", StringComparison.Ordinal), token.Replace("v1.", "v2.", StringComparison.Ordinal) })
        {
            Assert.False(rotated.TryRead(invalid, out _));
            var replacement = resolver.Resolve(null, invalid); Assert.True(rotated.TryRead(replacement.AnonymousVisitorId, out _));
            Assert.StartsWith("v1.new.", replacement.AnonymousVisitorId);
        }
        keys.Remove("old"); Assert.False(new AnonymousVisitorTokenService("new", keys).TryRead(token, out _));
        var actor = new CurrentActor(Guid.NewGuid(), []);
        var authenticated = resolver.Resolve(actor, token);
        Assert.Null(authenticated.AnonymousVisitorId); Assert.Equal(VisitorKind.Authenticated, authenticated.Identity.Kind);
        Assert.Equal(authenticated.Identity.Key.ToArray(), resolver.Resolve(actor, null).Identity.Key.ToArray());
        Assert.NotEqual(first.Identity.Key.ToArray(), authenticated.Identity.Key.ToArray());
    }

    [Fact]
    public void InvalidKeyRingsFailWithoutDisclosingSecrets()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        foreach (var invalid in new[] { "", "dot.key", "a b", new string('x', 33), "é", "line\n" })
        { Assert.Throws<ArgumentException>(() => new AnonymousVisitorTokenService(invalid, new Dictionary<string, string> { [invalid] = secret })); }
        foreach (var value in new[] { "", "bad base64", Convert.ToBase64String(new byte[31]), Convert.ToBase64String(new byte[33]) })
        { Assert.Throws<ArgumentException>(() => new AnonymousVisitorTokenService("key", new Dictionary<string, string> { ["key"] = value })); }
        Assert.Throws<ArgumentException>(() => new AnonymousVisitorTokenService("missing", Keys()));
        Assert.Throws<ArgumentException>(() => new AnonymousVisitorTokenService("key", new Dictionary<string, string>()));
        var max = new AnonymousVisitorTokenService(new string('a', 32), new Dictionary<string, string> { [new string('a', 32)] = secret });
        Assert.Equal(123, max.Issue().Length);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
