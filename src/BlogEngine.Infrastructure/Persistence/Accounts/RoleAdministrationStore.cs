using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using BlogEngine.Core.Accounts.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.Infrastructure.Persistence.Accounts;

public sealed class RoleAdministrationStore(BlogDbContext database) : IRoleAdministrationStore
{
    // Network-order UUID bytes and SHA-256 are stable across processes, runtimes and operating systems.
    // A 64-bit collision can only serialize additional users; it cannot bypass a user's lock.
    public static long LockKey(Guid userId) => BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(userId.ToByteArray(bigEndian: true)));

    public async Task<RoleUser?> FindAsync(VerifiedIdentity identity, CancellationToken cancellationToken)
    {
        var user = await database.LocalUsers.AsNoTracking().Where(u => u.Issuer == identity.Issuer && u.Subject == identity.Subject)
            .Select(u => new { u.Id, u.Issuer, u.Subject }).SingleOrDefaultAsync(cancellationToken);
        return user is null ? null : new(user.Id, user.Issuer, user.Subject, await RolesAsync(user.Id, cancellationToken));
    }

    public async Task<RoleUser?> GetAsync(Guid userId, string configuredIssuer, CancellationToken cancellationToken)
    {
        var user = await database.LocalUsers.AsNoTracking().Where(u => u.Id == userId && u.Issuer == configuredIssuer)
            .Select(u => new { u.Id, u.Issuer, u.Subject }).SingleOrDefaultAsync(cancellationToken);
        return user is null ? null : new(user.Id, user.Issuer, user.Subject, await RolesAsync(user.Id, cancellationToken));
    }

    public async Task<RoleChangeResult?> ChangeAsync(RoleChangeCommand command, string configuredIssuer, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await database.LocalUsers.AnyAsync(u => u.Id == command.UserId && u.Issuer == configuredIssuer, cancellationToken)) { return null; }
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey(command.UserId)})", cancellationToken);
        var changed = command.Action == RoleChangeAction.Grant
            ? await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO accounts.role_assignments (user_id, role, granted_at)
                VALUES ({command.UserId}, {(short)command.Role}, clock_timestamp()) ON CONFLICT (user_id, role) DO NOTHING
                """, cancellationToken)
            : await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id = {command.UserId} AND role = {(short)command.Role}", cancellationToken);
        var auditId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts.role_change_audit
                (id, command_correlation_id, target_user_id, role, action, changed, actor, reason, occurred_at)
            VALUES ({auditId}, {command.CorrelationId}, {command.UserId}, {(short)command.Role}, {(short)command.Action},
                {changed != 0}, {command.Actor}, {command.Reason}, clock_timestamp())
            """, cancellationToken);
        var result = new RoleChangeResult(command.UserId, await RolesAsync(command.UserId, cancellationToken), changed != 0, auditId, command.CorrelationId);
        cancellationToken.ThrowIfCancellationRequested();
        // Once COMMIT is sent, caller cancellation cannot establish rollback. Bound this exchange independently.
        using var commitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await transaction.CommitAsync(commitTimeout.Token); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new RoleAdministrationException("outcome_unknown");
        }
        return result;
    }

    private async Task<IReadOnlyList<LocalRole>> RolesAsync(Guid userId, CancellationToken cancellationToken) =>
        new CurrentActor(userId, await database.RoleAssignments.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.Role).ToArrayAsync(cancellationToken)).Roles;
}
