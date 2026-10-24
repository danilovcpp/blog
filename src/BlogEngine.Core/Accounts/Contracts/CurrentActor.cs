using System.Collections.Immutable;

namespace BlogEngine.Core.Accounts.Contracts;

public sealed record CurrentActor
{
    public CurrentActor(Guid id, IEnumerable<LocalRole> roles)
    {
        Id = id;
        var assignments = roles.ToArray();
        if (assignments.Any(role => !Enum.IsDefined(role))) { throw new ArgumentOutOfRangeException(nameof(roles)); }
        Roles = assignments.Append(LocalRole.Reader).Distinct().Order().ToImmutableArray();
    }

    public Guid Id { get; }
    public ImmutableArray<LocalRole> Roles { get; }
    public bool HasRole(LocalRole role) => Roles.Contains(role);
}

public sealed record VerifiedIdentity(string Issuer, Guid Subject);
