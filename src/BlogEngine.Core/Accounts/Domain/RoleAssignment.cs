using BlogEngine.Core.Accounts.Contracts;

namespace BlogEngine.Core.Accounts.Domain;

public sealed class RoleAssignment
{
    public RoleAssignment(Guid userId, LocalRole role, DateTimeOffset grantedAt)
    {
        if (role is not (LocalRole.Author or LocalRole.Moderator or LocalRole.Admin))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        if (grantedAt.Offset != TimeSpan.Zero) { throw new ArgumentException("UTC is required.", nameof(grantedAt)); }
        UserId = userId;
        Role = role;
        GrantedAt = grantedAt;
    }

    public Guid UserId { get; private set; }
    public LocalRole Role { get; private set; }
    public DateTimeOffset GrantedAt { get; private set; }
}
