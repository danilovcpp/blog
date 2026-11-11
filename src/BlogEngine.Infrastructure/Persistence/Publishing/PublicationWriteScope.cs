using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlogEngine.Infrastructure.Persistence.Publishing;

public sealed class PublicationWriteScope(BlogDbContext db, DatabaseClock? clock = null) : IPublicationWriteScope, IPublicationReader
{
    public async Task<T> ExecuteAsync<T>(Guid publicationId, Func<IPublicationWriteSession, CancellationToken, Task<T>> operation, CancellationToken ct, Guid? roleActorId = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var actor = roleActorId is { } actorId ? await new AccountRoleWriteGuard(db).LockAndReadAsync(actorId, ct) : null;
        var entity = await db.Publications.FromSqlInterpolated($"SELECT * FROM publishing.publications WHERE id = {publicationId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new DomainError("resource_not_found");
        // Refresh any previously tracked instance only after the lock, including state/version/counters.
        await db.Entry(entity).ReloadAsync(ct);
        var now = await (clock ?? new DatabaseClock()).ReadAsync(db, ct);
        var result = await operation(new PublicationWriteSession(db, entity, now, transaction, actor), ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    internal static Task<DateTimeOffset> UtcNowAsync(BlogDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    public Task<PublicationAccess?> FindAccessAsync(Guid publicationId, CancellationToken ct) => db.Publications.AsNoTracking()
        .Where(p => p.Id == publicationId).Select(p => new PublicationAccess(p.Id, p.AuthorId, p.State == PublicationState.Published, p.DeletedAt != null, p.Version)).SingleOrDefaultAsync(ct);
}

internal sealed class PublicationWriteSession(BlogDbContext db, Publication entity, DateTimeOffset now, IDbContextTransaction transaction, CurrentActor? actor) : IPublicationWriteSession
{
    internal Publication Entity => entity;
    public PublicationAccess Publication => new(entity.Id, entity.AuthorId, entity.State == PublicationState.Published, entity.DeletedAt is not null, entity.Version);
    public DateTimeOffset UtcNow => now;
    public CurrentActor? RoleActor => actor;
    public async Task AdjustCountersAsync(CounterDelta delta, CancellationToken ct)
    {
        if (!Publication.Published || Publication.Deleted) { throw new DomainError("resource_not_found"); }
        await PublicationCounterWriter.AdjustAsync(db, entity, delta, transaction, ct);
    }
}
