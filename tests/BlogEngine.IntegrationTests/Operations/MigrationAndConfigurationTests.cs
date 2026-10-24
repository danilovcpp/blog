using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlogEngine.IntegrationTests.Operations;

[Trait("Category", "Operations")]
public sealed class MigrationAndConfigurationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task InitialMigrationCanBeReappliedAfterRollbackAndMatchesTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        await using var context = BootstrapTests.Context(database.ConnectionString);
        var migrator = context.GetService<IMigrator>();
        Assert.Single(await context.Database.GetAppliedMigrationsAsync(ct));
        await migrator.MigrateAsync("0", ct);
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync(ct));
        await context.Database.MigrateAsync(ct);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync(ct));
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(0, await context.LocalUsers.CountAsync(ct));
        var constraints = await context.Database.SqlQueryRaw<string>("""
            SELECT conname AS "Value" FROM pg_constraint c
            JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'accounts'
            """).ToListAsync(ct);
        foreach (var expected in new[] { "pk_local_users", "uq_local_users_issuer_subject", "ck_local_users_issuer",
            "pk_role_assignments", "ck_role_assignments_role", "fk_role_assignments_local_users",
            "local_users_issuer_not_null", "local_users_subject_not_null", "role_assignments_granted_at_not_null" })
        {
            Assert.Contains(expected, constraints);
        }
    }

    [Fact]
    public void OperatorRequiresOnlyItsOwnIssuerAndConnectionWithoutAudienceOrJwks()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Issuer"] = "urn:operator-fixture",
            ["ConnectionStrings:Blog"] = ApiFactory.UnreachableDatabase,
        }).Build();
        var services = new ServiceCollection();
        OperatorOptions.Register(services, config);
        using var provider = services.BuildServiceProvider();
        Assert.Equal("urn:operator-fixture", provider.GetRequiredService<IOptions<OperatorOptions>>().Value.Issuer);
        var missing = new ServiceCollection();
        OperatorOptions.Register(missing, new ConfigurationBuilder().Build());
        using var invalid = missing.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => invalid.GetRequiredService<IOptions<OperatorOptions>>().Value);
    }
}
