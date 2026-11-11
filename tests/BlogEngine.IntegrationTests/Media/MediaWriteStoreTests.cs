using System.Data;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Accounts;
using BlogEngine.Infrastructure.Persistence.Media;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.IntegrationTests.Media;

public sealed class MediaWriteStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static BlogDbContext Context(string connection) => new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(connection).Options);
    private static MediaWriteStore Store(BlogDbContext db, DatabaseClock clock) => new(db, clock, new AccountRoleWriteGuard(db));

    [Fact]
    public async Task HundredConcurrentReservationsUseOwnerLockAndExcludeExactlyExpiredReservations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct);
        var owner = Guid.NewGuid(); var clock = new FixedClock(DateTimeOffset.UtcNow);
        await using (var db = Context(database.ConnectionString))
        {
            db.LocalUsers.Add(new LocalUser(owner, "https://fixture", Guid.NewGuid(), clock.Now)); await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO media.allowances(owner_id,limit_bytes,used_bytes) VALUES({owner},2000,0)", ct);
        }
        var barrier = new ConcurrencyBarrier(100);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await barrier.SignalAndWaitAsync(ct); await using var db = Context(database.ConnectionString);
            try
            {
                return await Store(db, clock).ExecuteAsync(new(owner), async (session, token) =>
                {
                    var file = MediaFile.Create(owner, MediaPurpose.Attachment, "same.bin", "application/octet-stream", 100, session.UtcNow);
                    await session.ReserveAsync(file, MediaUpload.Create(file, Guid.NewGuid(), null, session.UtcNow), token); return true;
                }, ct);
            }
            catch (DomainError error) when (error.Code == "quota_exceeded") { return false; }
        }));
        Assert.Equal(20, outcomes.Count(ok => ok));
        await using var verify = Context(database.ConnectionString);
        var quota = await Store(verify, clock).ExecuteAsync(new(owner), (session, token) => session.QuotaAsync(token), ct);
        Assert.Equal(new MediaQuota(2000, 0, 2000), quota); Assert.Equal(0, quota.Available);
        clock.Now = clock.Now.AddHours(24);
        quota = await Store(verify, clock).ExecuteAsync(new(owner), (session, token) => session.QuotaAsync(token), ct);
        Assert.Equal(0, quota.Reserved); Assert.Equal(2000, quota.Available);
    }

    [Fact]
    public async Task DurablePlanFencesStaleWritersAndRetirementAndCleanupRollbackTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct); await using var db = Context(database.ConnectionString);
        var owner = Guid.NewGuid(); var clock = new FixedClock(DateTimeOffset.UtcNow);
        db.LocalUsers.Add(new LocalUser(owner, "https://fixture", Guid.NewGuid(), clock.Now)); await db.SaveChangesAsync(ct);
        var store = Store(db, clock); var file = MediaFile.Create(owner, MediaPurpose.Attachment, "x", "text/plain", 1, clock.Now);
        var upload = MediaUpload.Create(file, Guid.NewGuid(), null, clock.Now);
        await store.ExecuteAsync(new(owner), async (s, t) => { await s.ReserveAsync(file, upload, t); return 0; }, ct);
        var generation = await store.ExecuteAsync(new(owner), (s, t) => s.ClaimUploadAsync(upload.Id, TimeSpan.FromSeconds(10), t), ct);
        var plan = MediaObject.Plan(upload, MediaObjectKind.Source, clock.Now);
        await store.ExecuteAsync(new(owner), async (s, t) => { await s.SaveObjectPlanAsync(plan, t); return 0; }, ct);
        await using (var observer = Context(database.ConnectionString)) { Assert.True(await observer.MediaObjects.AnyAsync(o => o.Id == plan.Id, ct)); }
        clock.Now = clock.Now.AddSeconds(10);
        var next = await store.ExecuteAsync(new(owner), (s, t) => s.ClaimUploadAsync(upload.Id, TimeSpan.FromSeconds(10), t), ct);
        Assert.True(next > generation);
        Assert.Equal("concurrency_conflict", (await Assert.ThrowsAsync<DomainError>(() => store.ExecuteAsync(new(owner), (s, t) => s.RequireFenceAsync(upload.Id, generation, t), ct))).Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync<int>(new(owner), async (s, t) =>
        { await s.RetireAsync(plan.Id, clock.Now, t); throw new InvalidOperationException("rollback"); }, ct));
        await using (var observer = Context(database.ConnectionString))
        { Assert.Empty(await observer.MediaCleanupJobs.ToArrayAsync(ct)); Assert.Equal(MediaObjectState.Planned, (await observer.MediaObjects.SingleAsync(ct)).State); }
        db.ChangeTracker.Clear();
        await store.ExecuteAsync(new(owner), async (s, t) => { await s.RetireAsync(plan.Id, clock.Now, t); return 0; }, ct);
        Assert.Single(await db.MediaCleanupJobs.AsNoTracking().ToArrayAsync(ct));
    }

    [Fact]
    public async Task RoleGuardRequiresCallersTransactionAndReadsRolesAfterTheSharedLock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct); await using var db = Context(database.ConnectionString);
        var owner = Guid.NewGuid();
        db.LocalUsers.Add(new LocalUser(owner, "https://fixture", Guid.NewGuid(), DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts.role_assignments(user_id,role,granted_at) VALUES({owner},{(short)LocalRole.Author},now())", ct);
        var guard = new AccountRoleWriteGuard(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.LockAndReadAsync(owner, ct));
        await using var revoker = Context(database.ConnectionString);
        await using var revocation = await revoker.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await revoker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({RoleAdministrationStore.LockKey(owner)})", ct);
        await revoker.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // The second statement is issued only after the revoker commits and releases the lock.
        var read = guard.LockAndReadAsync(owner, ct);
        await revocation.CommitAsync(ct);
        var actor = await read; Assert.False(actor.HasRole(LocalRole.Author));
        Assert.NotNull(db.Database.CurrentTransaction); await tx.RollbackAsync(ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : DatabaseClock
    {
        public DateTimeOffset Now { get; set; } = now;
        public override Task<DateTimeOffset> ReadAsync(BlogDbContext db, CancellationToken ct) => Task.FromResult(Now);
    }
}
