using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Accounts.Application;

public sealed class RoleAdministration(IRoleAdministrationStore store, string configuredIssuer)
{
    public Task<RoleUser?> FindAsync(VerifiedIdentity identity, CancellationToken cancellationToken)
    {
        if (!string.Equals(identity.Issuer, configuredIssuer, StringComparison.Ordinal)) { throw DomainError.Validation("issuer"); }
        return store.FindAsync(identity, cancellationToken);
    }

    public async Task<RoleUser> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        await store.GetAsync(userId, configuredIssuer, cancellationToken) ?? throw new DomainError("user_not_found");

    public async Task<RoleChangeResult> ChangeAsync(RoleChangeCommand command, CancellationToken cancellationToken) =>
        await store.ChangeAsync(command, configuredIssuer, cancellationToken) ?? throw new DomainError("user_not_found");
}
