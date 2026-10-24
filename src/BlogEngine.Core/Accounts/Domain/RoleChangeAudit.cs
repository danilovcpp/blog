using BlogEngine.Core.Accounts.Contracts;

namespace BlogEngine.Core.Accounts.Domain;

public sealed class RoleChangeAudit
{
    private RoleChangeAudit() { }

    public RoleChangeAudit(Guid id, RoleChangeCommand command, bool changed, DateTimeOffset occurredAt)
    {
        if (occurredAt.Offset != TimeSpan.Zero) { throw new ArgumentException("UTC is required.", nameof(occurredAt)); }
        Id = id;
        CommandCorrelationId = command.CorrelationId;
        TargetUserId = command.UserId;
        Role = command.Role;
        Action = command.Action;
        Changed = changed;
        Actor = command.Actor;
        Reason = command.Reason;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid CommandCorrelationId { get; private set; }
    public Guid TargetUserId { get; private set; }
    public LocalRole Role { get; private set; }
    public RoleChangeAction Action { get; private set; }
    public bool Changed { get; private set; }
    public string Actor { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
}
