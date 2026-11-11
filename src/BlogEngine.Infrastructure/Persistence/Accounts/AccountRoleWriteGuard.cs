using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlogEngine.Infrastructure.Persistence.Accounts;

public sealed class AccountRoleWriteGuard(BlogDbContext db) : IAccountRoleWriteGuard
{
    public async Task<CurrentActor> LockAndReadAsync(Guid verifiedUserId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
        { throw new InvalidOperationException("The role guard requires the caller's READ COMMITTED transaction."); }
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({RoleAdministrationStore.LockKey(verifiedUserId)})", ct);
        // A separate statement sees revocations that committed while the lock was being acquired.
        var roles = await db.RoleAssignments.AsNoTracking().Where(r => r.UserId == verifiedUserId).Select(r => r.Role).ToArrayAsync(ct);
        return new CurrentActor(verifiedUserId, roles);
    }
}
