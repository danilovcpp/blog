using System.Collections.Immutable;
using Microsoft.IdentityModel.Tokens;

namespace BlogEngine.Infrastructure.Identity;

public sealed record JwksSnapshot(ImmutableDictionary<string, SecurityKey> Keys, DateTimeOffset ExpiresAt)
{
    public bool IsFresh(DateTimeOffset now) => now < ExpiresAt && !Keys.IsEmpty;
}

public sealed record JwksSettings(Uri Url);

public sealed class IdentityKeysUnavailableException() : Exception("Identity signing keys are unavailable.");
