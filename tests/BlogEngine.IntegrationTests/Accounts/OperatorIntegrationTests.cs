using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BlogEngine.IntegrationTests.Accounts;

[Trait("Category", "Authorization")]
[Trait("Category", "Concurrency")]
[Trait("Category", "Operations")]
public sealed class OperatorIntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RealConsoleSupportsFourCommandsSafeErrorsNoOpsAndAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await OperatorFixture.StartAsync(postgres, ct);
        var found = (await f.RunAsync(["users", "find", "--issuer", OperatorFixture.Issuer, "--subject", f.Subject.ToString()], ct)).Success();
        Assert.Equal(["userId", "issuer", "subject", "roles"], found.EnumerateObject().Select(p => p.Name));
        Assert.Equal(f.UserId, found.GetProperty("userId").GetGuid());
        var list = (await f.RunAsync(["roles", "list", "--user-id", f.UserId.ToString()], ct)).Success();
        Assert.Equal("[\"Reader\"]", list.GetProperty("roles").GetRawText());
        var correlations = new HashSet<Guid>();
        foreach (var role in new[] { "Author", "Moderator", "Admin" })
        {
            foreach (var action in new[] { "grant", "grant", "revoke", "revoke" })
            {
                var result = (await f.RunAsync(f.Change(action, role), ct)).Success();
                Assert.Equal(["userId", "roles", "changed", "auditId", "correlationId"], result.EnumerateObject().Select(p => p.Name));
                Assert.True(correlations.Add(result.GetProperty("correlationId").GetGuid()));
                Assert.NotEqual(Guid.Empty, result.GetProperty("auditId").GetGuid());
            }
        }
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        Assert.Equal(12, await db.RoleChangeAudits.CountAsync(ct));
        Assert.Equal(6, await db.RoleChangeAudits.CountAsync(a => a.Changed, ct));
        Assert.All(await db.RoleChangeAudits.AsNoTracking().ToArrayAsync(ct), a =>
        { Assert.False(string.IsNullOrWhiteSpace(a.Actor)); Assert.Equal("fixture reason 😀", a.Reason); Assert.Equal(TimeSpan.Zero, a.OccurredAt.Offset); });
        Assert.Empty(await db.RoleAssignments.ToArrayAsync(ct));
        (await f.RunAsync(["roles", "list", "--user-id", Guid.NewGuid().ToString()], ct)).Error(3, "user_not_found");
        (await f.RunAsync(["roles", "list", "--user-id", "bad"], ct, ApiFactory.UnreachableDatabase)).Error(2, "invalid_arguments");
        (await f.RunAsync(["users", "find", "--issuer", OperatorFixture.Issuer + "/", "--subject", f.Subject.ToString()], ct, ApiFactory.UnreachableDatabase)).Error(2, "invalid_arguments");
        (await f.RunAsync(f.Change(), ct, issuer: null)).Error(5, "invalid_configuration");
        (await f.RunAsync(f.Change(), ct, environment: new Dictionary<string, string> { ["Operator__TimeoutSeconds"] = "not-an-integer" })).Error(5, "invalid_configuration");
        (await f.RunAsync(f.Change(), ct, timeoutSeconds: 0)).Error(5, "invalid_configuration");
        (await f.RunAsync(f.Change(), ct, ApiFactory.UnreachableDatabase)).Error(4, "database_error");
        (await f.RunAsync(f.Change(), ct, f.RuntimeConnectionString)).Error(4, "database_error");
        Assert.Equal(12, await db.RoleChangeAudits.CountAsync(ct));
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("us3-security.json"), JsonSerializer.Serialize(new
        { scenario = "SC-007 / US3", realPostgres = "18.6", principals = new[] { "runtime", "operator", "migration", "owner" }, cliCommands = 14, auditRows = 12, uniqueCorrelations = 12, changed = 6, noOp = 6, exitCodes = new[] { 0, 2, 3, 4, 5 }, result = "PASS" }), ct);
    }

    [Fact]
    public async Task ConcurrentCommandsSerializeAndAuditFailureRollsBackMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await OperatorFixture.StartAsync(postgres, ct);
        var barrier = new ConcurrencyBarrier(12);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ => { await barrier.SignalAndWaitAsync(ct); return await f.RunAsync(f.Change(), ct); }));
        Assert.Equal(1, results.Count(r => r.Success().GetProperty("changed").GetBoolean()));
        Assert.Equal(12, results.Select(r => r.Success().GetProperty("correlationId").GetGuid()).Distinct().Count());
        var mixedBarrier = new ConcurrencyBarrier(12);
        var mixed = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i => { await mixedBarrier.SignalAndWaitAsync(ct); return await f.RunAsync(f.Change(i % 2 == 0 ? "grant" : "revoke"), ct); }));
        Assert.All(mixed, r => r.Success());
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        var audits = await db.RoleChangeAudits.AsNoTracking().OrderBy(a => a.OccurredAt).ToArrayAsync(ct);
        var assigned = false;
        foreach (var a in audits)
        {
            var next = a.Action == RoleChangeAction.Grant;
            Assert.Equal(assigned != next, a.Changed);
            assigned = next;
        }
        Assert.Equal(assigned, await db.RoleAssignments.AnyAsync(ct));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE accounts.role_change_audit ADD CONSTRAINT fixture_reject_audit CHECK (reason <> 'fixture reason 😀') NOT VALID", ct);
        (await f.RunAsync(f.Change(role: "Moderator"), ct)).Error(4, "database_error");
        Assert.False(await db.RoleAssignments.AnyAsync(a => a.Role == LocalRole.Moderator, ct));
        Assert.Equal(24, await db.RoleChangeAudits.CountAsync(ct));
    }

    [Fact]
    public async Task TimeoutBeforeCommitIsConfirmedCancellationWithoutWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await OperatorFixture.StartAsync(postgres, ct);
        await using var blocker = BootstrapTests.Context(f.Database.ConnectionString);
        await using var transaction = await blocker.Database.BeginTransactionAsync(ct);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({RoleAdministrationStore.LockKey(f.UserId)})", ct);
        (await f.RunAsync(f.Change(), ct, timeoutSeconds: 1)).Error(130, "cancelled");
        if (OperatingSystem.IsLinux())
        {
            var application = "signal_" + Guid.NewGuid().ToString("N");
            var signalConnection = new NpgsqlConnectionStringBuilder(f.ConnectionString) { ApplicationName = application }.ConnectionString;
            var signalled = await f.RunAsync(f.Change(), ct, signalConnection, onStarted: async process =>
            {
                // Observe the actual advisory-lock wait before signaling; no sleeps or timing assumptions.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await using var observer = new NpgsqlConnection(f.Database.ConnectionString); await observer.OpenAsync(deadline.Token);
                await using var wait = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name=@application AND wait_event='advisory')", observer);
                wait.Parameters.AddWithValue("application", application);
                while (!(bool)(await wait.ExecuteScalarAsync(deadline.Token))!) { deadline.Token.ThrowIfCancellationRequested(); }
                var start = new ProcessStartInfo("kill") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("-INT"); start.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var signal = Process.Start(start)!;
                await signal.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, signal.ExitCode);
            });
            signalled.Error(130, "cancelled");
        }
        Assert.Equal(0, await blocker.RoleAssignments.CountAsync(ct));
        Assert.Equal(0, await blocker.RoleChangeAudits.CountAsync(ct));
    }

    [Fact]
    public async Task RealConsoleLosingConnectionDuringCommitReportsUnknownWithSearchableCorrelation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await OperatorFixture.StartAsync(postgres, ct);
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        // A fixture-only deferred trigger disconnects this command during COMMIT, before its outcome can be acknowledged.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION public.fixture_commit_loss() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
            BEGIN PERFORM pg_terminate_backend(pg_backend_pid()); RETURN NEW; END $$;
            CREATE CONSTRAINT TRIGGER fixture_commit_loss AFTER INSERT ON accounts.role_change_audit
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fixture_commit_loss();
            """, ct);
        var error = (await f.RunAsync(f.Change(), ct)).Error(4, "outcome_unknown");
        var correlation = error.GetProperty("correlationId").GetGuid();
        // Inspection establishes rollback; the disconnected client could not establish this on its own.
        Assert.False(await db.RoleChangeAudits.AnyAsync(a => a.CommandCorrelationId == correlation, ct));
        Assert.False(await db.RoleAssignments.AnyAsync(ct));
    }

    [Fact]
    public async Task CommitAcknowledgementLossIsUnknownAndPostCommitCancellationIsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await OperatorFixture.StartAsync(postgres, ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var interceptor = new CommitObservation(cancellation);
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(f.ConnectionString).AddInterceptors(interceptor).Options);
        var store = new RoleAdministrationStore(db);
        var correlation = Guid.NewGuid();
        interceptor.LoseAcknowledgement = true;
        var error = await Assert.ThrowsAsync<RoleAdministrationException>(() => store.ChangeAsync(new(f.UserId, LocalRole.Author, RoleChangeAction.Grant, "why", "fixture", correlation), OperatorFixture.Issuer, ct));
        Assert.Equal("outcome_unknown", error.Code);
        await using var inspect = BootstrapTests.Context(f.Database.ConnectionString);
        Assert.True(await inspect.RoleChangeAudits.AnyAsync(a => a.CommandCorrelationId == correlation, ct));
        Assert.True(await inspect.RoleAssignments.AnyAsync(ct));
        interceptor.LoseAcknowledgement = false;
        interceptor.CancelAfterCommit = true;
        var result = await store.ChangeAsync(new(f.UserId, LocalRole.Author, RoleChangeAction.Revoke, "why", "fixture", Guid.NewGuid()), OperatorFixture.Issuer, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result!.Changed);
        Assert.False(await inspect.RoleAssignments.AnyAsync(ct));
    }

    [Fact]
    public async Task CancellationAfterAuditBeforeCommitRollsBackEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await OperatorFixture.StartAsync(postgres, ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(f.ConnectionString).AddInterceptors(new CancelAfterAudit(cancellation)).Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RoleAdministrationStore(db).ChangeAsync(new(f.UserId, LocalRole.Author, RoleChangeAction.Grant, "why", "fixture", Guid.NewGuid()), OperatorFixture.Issuer, cancellation.Token));
        await using var inspect = BootstrapTests.Context(f.Database.ConnectionString);
        Assert.Empty(await inspect.RoleAssignments.ToArrayAsync(ct));
        Assert.Empty(await inspect.RoleChangeAudits.ToArrayAsync(ct));
    }

    [Fact]
    public async Task AuditMigrationSupportsFreshUpgradeRollbackAndDatabaseConstraints()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        await using var db = BootstrapTests.Context(database.ConnectionString);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("AddPublishing", ct);
        var baseline = new BlogEngine.Core.Accounts.Domain.LocalUser(Guid.NewGuid(), OperatorFixture.Issuer, Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.LocalUsers.Add(baseline);
        db.RoleAssignments.Add(new(baseline.Id, LocalRole.Moderator, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        await db.Database.MigrateAsync(ct);
        Assert.True(await db.LocalUsers.AnyAsync(u => u.Id == baseline.Id, ct));
        Assert.True(await db.RoleAssignments.AnyAsync(a => a.UserId == baseline.Id && a.Role == LocalRole.Moderator, ct));
        var script = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("us3-migrations.sql"), script, ct);
        await migrator.MigrateAsync("0", ct);
        db.ChangeTracker.Clear();
        // Execute the reviewed, generated idempotent SQL itself on empty and then already-upgraded schemas.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var apply = new NpgsqlCommand(script, connection);
            await apply.ExecuteNonQueryAsync(ct);
            await apply.ExecuteNonQueryAsync(ct);
        }
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(ct));
        var user = new BlogEngine.Core.Accounts.Domain.LocalUser(Guid.NewGuid(), OperatorFixture.Issuer, Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.LocalUsers.Add(user); await db.SaveChangesAsync(ct);
        var correlation = Guid.NewGuid();
        var store = new RoleAdministrationStore(db);
        await store.ChangeAsync(new(user.Id, LocalRole.Admin, RoleChangeAction.Grant, string.Concat(Enumerable.Repeat("😀", 512)), "fixture", correlation), OperatorFixture.Issuer, ct);
        foreach (var (sql, state) in new[]
        {
            ("UPDATE accounts.role_change_audit SET reason=' '", "23514"),
            ("UPDATE accounts.role_change_audit SET reason=E'\\tbad'", "23514"),
            ("UPDATE accounts.role_change_audit SET reason=repeat('a',513)", "23514"),
            ("UPDATE accounts.role_change_audit SET role=0", "23514"),
            ("UPDATE accounts.role_change_audit SET action=3", "23514"),
            ("UPDATE accounts.role_change_audit SET actor=' '", "23514"),
            ("UPDATE accounts.role_change_audit SET actor=NULL", "23502"),
            ("UPDATE accounts.role_change_audit SET reason=NULL", "23502"),
            ("UPDATE accounts.role_change_audit SET command_correlation_id=NULL", "23502"),
            ("UPDATE accounts.role_change_audit SET target_user_id=NULL", "23502"),
            ("UPDATE accounts.role_change_audit SET role=NULL", "23502"),
            ("UPDATE accounts.role_change_audit SET action=NULL", "23502"),
            ("UPDATE accounts.role_change_audit SET changed=NULL", "23502"),
            ("UPDATE accounts.role_change_audit SET occurred_at=NULL", "23502"),
            ("UPDATE accounts.role_change_audit SET target_user_id=gen_random_uuid()", "23503"),
            ("INSERT INTO accounts.role_change_audit SELECT gen_random_uuid(),command_correlation_id,target_user_id,role,action,changed,actor,reason,occurred_at FROM accounts.role_change_audit", "23505"),
            ("DELETE FROM accounts.local_users", "23001"),
        })
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState);
        }
    }

    private sealed class CommitObservation(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public bool LoseAcknowledgement { get; set; }
        public bool CancelAfterCommit { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (LoseAcknowledgement) { throw new IOException("simulated lost commit acknowledgement"); }
            if (CancelAfterCommit) { cancellation.Cancel(); }
            return Task.CompletedTask;
        }
    }

    private sealed class CancelAfterAudit(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO accounts.role_change_audit", StringComparison.Ordinal)) { cancellation.Cancel(); }
            return ValueTask.FromResult(result);
        }
    }
}
