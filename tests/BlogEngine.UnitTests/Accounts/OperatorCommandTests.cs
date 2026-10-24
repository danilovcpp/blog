using System.Text.Json;
using BlogEngine.Core.Accounts.Application;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;
using BlogEngine.Operator;

namespace BlogEngine.UnitTests.Accounts;

public sealed class OperatorCommandTests
{
    private const string Issuer = "urn:operator-fixture";
    private static readonly Guid UserId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void FourCommandsParseWithExactIssuerAndNormalizedReason()
    {
        Assert.Equal(OperatorCommandKind.Find, CommandParser.Parse(["users", "find", "--issuer", Issuer, "--subject", UserId.ToString()], Issuer).Kind);
        Assert.Equal(OperatorCommandKind.List, CommandParser.Parse(["roles", "list", "--user-id", UserId.ToString()], Issuer).Kind);
        foreach (var action in new[] { "grant", "revoke" })
        {
            foreach (var role in new[] { "Author", "Moderator", "Admin" })
            {
                var command = CommandParser.Parse(["roles", action, "--user-id", UserId.ToString(), "--role", role, "--reason", "\u0085 reason 😀 \u3000"], Issuer);
                Assert.Equal("reason 😀", command.Reason);
                Assert.Equal(Enum.Parse<LocalRole>(role), command.Role);
            }
        }
        var maximum = string.Concat(Enumerable.Repeat("😀", 512));
        Assert.Equal(maximum, CommandParser.Parse(["roles", "grant", "--user-id", UserId.ToString(), "--role", "Admin", "--reason", maximum], Issuer).Reason);
    }

    public static IEnumerable<object[]> InvalidCommands()
    {
        yield return [Array.Empty<string>()];
        yield return [new[] { "users", "create" }];
        yield return [new[] { "users", "find", "--issuer", Issuer + "/", "--subject", UserId.ToString() }];
        yield return [new[] { "users", "find", "--issuer", Issuer.ToUpperInvariant(), "--subject", UserId.ToString() }];
        yield return [new[] { "roles", "list", "--user-id", "invalid" }];
        yield return [new[] { "roles", "list", "--user-id", UserId.ToString(), "--user-id", UserId.ToString() }];
        yield return [new[] { "roles", "list", "--user-id", UserId.ToString(), "--actor", "admin" }];
        foreach (var role in new[] { "Reader", "author", "1", "4" })
        {
            yield return [new[] { "roles", "grant", "--user-id", UserId.ToString(), "--role", role, "--reason", "test" }];
        }
        foreach (var reason in new[] { "\u0085\u3000", new string('a', 513), "x\0y" })
        {
            yield return [new[] { "roles", "grant", "--user-id", UserId.ToString(), "--role", "Admin", "--reason", reason }];
        }
        yield return [new[] { "roles", "revoke", "--user-id", UserId.ToString(), "--role", "Admin" }];
        yield return [new[] { "roles", "list", "--user-id", UserId.ToString(), "--unknown", "x" }];
        yield return [new[] { "roles", "list", "--user-id" }];
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void InvalidCommandsAreRejectedBeforeAnyStoreAccess(string[] arguments) =>
        Assert.Throws<DomainError>(() => CommandParser.Parse(arguments, Issuer));

    [Fact]
    public async Task AdministrationRequiresExistingUserAndKeepsNoOpAndIndependentRoles()
    {
        var store = new MemoryStore(UserId, Issuer);
        var administration = new RoleAdministration(store, Issuer);
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await administration.FindAsync(new VerifiedIdentity(Issuer, Guid.Empty), ct));
        await Assert.ThrowsAsync<DomainError>(() => administration.ListAsync(Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<DomainError>(() => administration.FindAsync(new VerifiedIdentity(Issuer + "/", UserId), ct));
        foreach (var role in new[] { LocalRole.Author, LocalRole.Moderator, LocalRole.Admin })
        {
            var result = await administration.ChangeAsync(new(UserId, role, RoleChangeAction.Grant, "reason", "os-user", Guid.NewGuid()), ct);
            Assert.True(result.Changed);
            Assert.False((await administration.ChangeAsync(new(UserId, role, RoleChangeAction.Grant, "reason", "os-user", Guid.NewGuid()), ct)).Changed);
        }
        Assert.Equal([LocalRole.Reader, LocalRole.Author, LocalRole.Moderator, LocalRole.Admin], (await administration.ListAsync(UserId, ct)).Roles);
        foreach (var role in new[] { LocalRole.Admin, LocalRole.Moderator, LocalRole.Author })
        {
            Assert.True((await administration.ChangeAsync(new(UserId, role, RoleChangeAction.Revoke, "reason", "os-user", Guid.NewGuid()), ct)).Changed);
            Assert.False((await administration.ChangeAsync(new(UserId, role, RoleChangeAction.Revoke, "reason", "os-user", Guid.NewGuid()), ct)).Changed);
        }
        Assert.Equal(12, store.Audits);
        Assert.Equal([LocalRole.Reader], (await administration.ListAsync(UserId, ct)).Roles);
    }

    [Fact]
    public void DomainRejectsInvalidRoleActionReasonAndActor()
    {
        // Keep invalid UTF-16 out of theory-data serialization, which replaces unpaired surrogates.
        Assert.Throws<DomainError>(() => CommandParser.Parse(["roles", "grant", "--user-id", UserId.ToString(), "--role", "Admin", "--reason", "\ud800"], Issuer));
        Assert.Throws<DomainError>(() => new RoleChangeCommand(UserId, LocalRole.Reader, RoleChangeAction.Grant, "why", "os", Guid.NewGuid()));
        Assert.Throws<DomainError>(() => new RoleChangeCommand(UserId, LocalRole.Admin, (RoleChangeAction)3, "why", "os", Guid.NewGuid()));
        Assert.Throws<DomainError>(() => new RoleChangeCommand(UserId, LocalRole.Admin, RoleChangeAction.Grant, " ", "os", Guid.NewGuid()));
        Assert.Throws<DomainError>(() => new RoleChangeCommand(UserId, LocalRole.Admin, RoleChangeAction.Grant, "why", " ", Guid.NewGuid()));
    }

    [Fact]
    public async Task ResultWriterEmitsOneSafeObjectWithCorrelation()
    {
        using var writer = new StringWriter();
        var correlation = Guid.NewGuid();
        await CommandResultWriter.WriteErrorAsync(writer, "outcome_unknown", correlation);
        using var json = JsonDocument.Parse(writer.ToString());
        Assert.Equal(["code", "message", "correlationId"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(correlation, json.RootElement.GetProperty("correlationId").GetGuid());
        Assert.DoesNotContain("Exception", writer.ToString(), StringComparison.Ordinal);
    }

    private sealed class MemoryStore(Guid id, string issuer) : IRoleAdministrationStore
    {
        private readonly HashSet<LocalRole> roles = [];
        public int Audits { get; private set; }
        private RoleUser User => new(id, issuer, id, new CurrentActor(id, roles).Roles);
        public Task<RoleUser?> FindAsync(VerifiedIdentity identity, CancellationToken cancellationToken) => Task.FromResult(identity.Subject == id ? User : null);
        public Task<RoleUser?> GetAsync(Guid userId, string configuredIssuer, CancellationToken cancellationToken) => Task.FromResult(userId == id && issuer == configuredIssuer ? User : null);
        public Task<RoleChangeResult?> ChangeAsync(RoleChangeCommand command, string configuredIssuer, CancellationToken cancellationToken)
        {
            if (command.UserId != id) { return Task.FromResult<RoleChangeResult?>(null); }
            var changed = command.Action == RoleChangeAction.Grant ? roles.Add(command.Role) : roles.Remove(command.Role);
            Audits++;
            return Task.FromResult<RoleChangeResult?>(new(id, User.Roles, changed, Guid.NewGuid(), command.CorrelationId));
        }
    }
}
