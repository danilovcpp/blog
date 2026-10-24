namespace BlogEngine.Core.Accounts.Contracts;

public interface IActorResolver
{
    Task<CurrentActor> ResolveAsync(VerifiedIdentity identity, CancellationToken cancellationToken);
}
