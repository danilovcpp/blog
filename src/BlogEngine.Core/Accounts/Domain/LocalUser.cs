namespace BlogEngine.Core.Accounts.Domain;

public sealed class LocalUser
{
    public LocalUser(Guid id, string issuer, Guid subject, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(issuer);
        if (createdAt.Offset != TimeSpan.Zero) { throw new ArgumentException("UTC is required.", nameof(createdAt)); }
        Id = id;
        Issuer = issuer;
        Subject = subject;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Issuer { get; private set; }
    public Guid Subject { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
