using System.Security.Cryptography;
using DotNet.Testcontainers.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BlogEngine.IntegrationTests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(PostgresImage.Reference)
        .WithDatabase("postgres")
        .WithUsername("fixture_admin")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
        .WithEnvironment("POSTGRES_INITDB_ARGS", "--encoding=UTF8 --locale=C")
        .WithEnvironment("TZ", "UTC")
        .WithEnvironment("PGTZ", "UTC")
        .WithCommand(new OverwriteEnumerable<string>(["postgres", "-c", "timezone=UTC", "-c", "max_connections=200"]))
        .Build();

    public async ValueTask InitializeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await container.StartAsync(timeout.Token);
    }

    public async Task<PostgresDatabase> CreateDatabaseAsync<TContext>(
        Func<string, TContext> createContext, CancellationToken cancellationToken) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(createContext);
        var databaseName = "test_" + Guid.NewGuid().ToString("N");
        var adminConnectionString = GetAdminConnectionString();
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(cancellationToken);
        // PostgreSQL identifiers cannot be parameters. This identifier contains only our generated hex.
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\" TEMPLATE template0 ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C'", admin);
        await command.ExecuteNonQueryAsync(cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            // Reuse sequential connections so repeated 100-way HTTP scenarios do not exhaust
            // the host's ephemeral TCP ports. Simultaneous contexts still rent separate connections.
            Pooling = true,
            Timezone = "UTC",
        }.ConnectionString;
        var database = new PostgresDatabase(connectionString, adminConnectionString, databaseName);
        try
        {
            await using var context = createContext(connectionString);
            if (!context.Database.GetMigrations().Any())
            {
                throw new InvalidOperationException("The fixture requires EF migrations; EnsureCreated is not supported.");
            }

            await context.Database.MigrateAsync(cancellationToken);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();

    private string GetAdminConnectionString()
    {
        // This isolated password-authenticated server has no Kerberos installation.
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString;
    }

    public async Task ApplyPermissionsAsync(PostgresDatabase database, IReadOnlyDictionary<string, string> roles, CancellationToken cancellationToken)
    {
        var target = "/tmp/permissions_" + Guid.NewGuid().ToString("N") + ".sql";
        await container.CopyAsync(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "permissions.sql"), cancellationToken),
            target, ct: cancellationToken);
        var arguments = new List<string> { "psql", "--username", "fixture_admin", "--dbname", new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!, "--file", target };
        foreach (var (key, value) in roles) { arguments.AddRange(["--variable", key + "=" + value]); }
        var result = await container.ExecAsync(arguments, cancellationToken);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }
}

public sealed class PostgresDatabase : IAsyncDisposable
{
    private readonly string adminConnectionString;
    private readonly string databaseName;

    internal PostgresDatabase(string connectionString, string adminConnectionString, string databaseName)
    {
        ConnectionString = connectionString;
        this.adminConnectionString = adminConnectionString;
        this.databaseName = databaseName;
    }

    public string ConnectionString { get; }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Close this isolated database's idle pool before dropping it; active connections close on return.
        using var databasePool = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(databasePool);
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync(timeout.Token);
    }
}
