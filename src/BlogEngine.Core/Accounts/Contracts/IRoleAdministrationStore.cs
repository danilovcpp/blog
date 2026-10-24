namespace BlogEngine.Core.Accounts.Contracts;

public interface IRoleAdministrationStore
{
    Task<RoleUser?> FindAsync(VerifiedIdentity identity, CancellationToken cancellationToken);
    Task<RoleUser?> GetAsync(Guid userId, string configuredIssuer, CancellationToken cancellationToken);
    Task<RoleChangeResult?> ChangeAsync(RoleChangeCommand command, string configuredIssuer, CancellationToken cancellationToken);
}
