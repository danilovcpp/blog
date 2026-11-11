namespace BlogEngine.Core.Accounts.Contracts;

// Joins the caller's READ COMMITTED transaction; the advisory lock lives until that transaction ends.
public interface IAccountRoleWriteGuard
{
    Task<CurrentActor> LockAndReadAsync(Guid verifiedUserId, CancellationToken cancellationToken);
}
