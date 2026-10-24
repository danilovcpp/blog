using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Accounts.Contracts;

public enum RoleChangeAction : short { Grant = 1, Revoke = 2 }

public sealed record RoleChangeCommand
{
    public RoleChangeCommand(Guid userId, LocalRole role, RoleChangeAction action, string reason, string actor, Guid correlationId)
    {
        if (role is not (LocalRole.Author or LocalRole.Moderator or LocalRole.Admin)) { throw DomainError.Validation("role"); }
        if (action is not (RoleChangeAction.Grant or RoleChangeAction.Revoke)) { throw DomainError.Validation("action"); }
        if (TextRules.ScalarLength(TextRules.TrimWhiteSpace(actor), "actor") == 0) { throw DomainError.Validation("actor"); }
        UserId = userId;
        Role = role;
        Action = action;
        Reason = NormalizeReason(reason);
        Actor = actor;
        CorrelationId = correlationId;
    }

    public static string NormalizeReason(string reason) => TextRules.RequireTrimmed(reason, 512, "reason");
    public Guid UserId { get; }
    public LocalRole Role { get; }
    public RoleChangeAction Action { get; }
    public string Reason { get; }
    public string Actor { get; }
    public Guid CorrelationId { get; }
}

public sealed record RoleUser(Guid UserId, string Issuer, Guid Subject, IReadOnlyList<LocalRole> Roles);
public sealed record RoleChangeResult(Guid UserId, IReadOnlyList<LocalRole> Roles, bool Changed, Guid AuditId, Guid CorrelationId);

public sealed class RoleAdministrationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
