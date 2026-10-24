using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Accounts;

public sealed class ActorResolver(BlogDbContext database) : IActorResolver
{
    public async Task<CurrentActor> ResolveAsync(VerifiedIdentity identity, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var id = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts.local_users (id, issuer, subject, created_at)
            VALUES ({id}, {identity.Issuer}, {identity.Subject}, clock_timestamp())
            ON CONFLICT (issuer, subject) DO NOTHING
            """, cancellationToken);
        // A separate READ COMMITTED statement sees the winner after a concurrent INSERT has committed.
        var localId = await database.LocalUsers.AsNoTracking()
            .Where(user => user.Issuer == identity.Issuer && user.Subject == identity.Subject)
            .Select(user => user.Id).SingleAsync(cancellationToken);
        var roles = await database.RoleAssignments.AsNoTracking().Where(role => role.UserId == localId)
            .Select(role => role.Role).ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CurrentActor(localId, roles);
    }
}
