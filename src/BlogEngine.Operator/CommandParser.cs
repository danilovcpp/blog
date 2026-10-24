using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Operator;

public enum OperatorCommandKind { Find, List, Grant, Revoke }
public sealed record OperatorCommand(OperatorCommandKind Kind, Guid Id, LocalRole? Role = null, string? Reason = null);

public static class CommandParser
{
    public static OperatorCommand Parse(IReadOnlyList<string> arguments, string configuredIssuer)
    {
        if (arguments.Count < 2 || arguments.Count % 2 != 0) { throw Invalid(); }
        var kind = (arguments[0], arguments[1]) switch
        {
            ("users", "find") => OperatorCommandKind.Find,
            ("roles", "list") => OperatorCommandKind.List,
            ("roles", "grant") => OperatorCommandKind.Grant,
            ("roles", "revoke") => OperatorCommandKind.Revoke,
            _ => throw Invalid(),
        };
        string[] required = kind switch
        {
            OperatorCommandKind.Find => ["--issuer", "--subject"],
            OperatorCommandKind.List => ["--user-id"],
            _ => ["--user-id", "--role", "--reason"],
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < arguments.Count; i += 2)
        {
            if (!required.Contains(arguments[i], StringComparer.Ordinal) || !values.TryAdd(arguments[i], arguments[i + 1])) { throw Invalid(); }
        }
        if (values.Count != required.Length || !Guid.TryParseExact(values[kind == OperatorCommandKind.Find ? "--subject" : "--user-id"], "D", out var id)) { throw Invalid(); }
        if (kind == OperatorCommandKind.Find && !string.Equals(values["--issuer"], configuredIssuer, StringComparison.Ordinal)) { throw Invalid(); }
        if (kind is OperatorCommandKind.Find or OperatorCommandKind.List) { return new(kind, id); }
        var role = values["--role"] switch
        {
            "Author" => LocalRole.Author,
            "Moderator" => LocalRole.Moderator,
            "Admin" => LocalRole.Admin,
            _ => throw Invalid(),
        };
        try { return new(kind, id, role, RoleChangeCommand.NormalizeReason(values["--reason"])); }
        catch (DomainError) { throw Invalid(); }
    }

    private static DomainError Invalid() => new("invalid_arguments");
}
