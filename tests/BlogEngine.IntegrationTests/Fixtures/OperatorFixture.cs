using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BlogEngine.Core.Accounts.Domain;
using BlogEngine.IntegrationTests.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlogEngine.IntegrationTests.Fixtures;

internal sealed class OperatorFixture : IAsyncDisposable
{
    public const string Issuer = "urn:operator-integration";
    private OperatorFixture(PostgresDatabase database, Dictionary<string, string> roles, string connection, string runtime, string migration, Guid id, Guid subject)
    { Database = database; Roles = roles; ConnectionString = connection; RuntimeConnectionString = runtime; MigrationConnectionString = migration; UserId = id; Subject = subject; }
    public PostgresDatabase Database { get; }
    public Dictionary<string, string> Roles { get; }
    public string ConnectionString { get; }
    public string RuntimeConnectionString { get; }
    public string MigrationConnectionString { get; }
    public Guid UserId { get; }
    public Guid Subject { get; }

    public static async Task<OperatorFixture> StartAsync(PostgresFixture postgres, CancellationToken ct)
    {
        var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        var roles = new[] { "owner_role", "migration_role", "runtime_role", "operator_role" }.ToDictionary(k => k, _ => "op_" + Guid.NewGuid().ToString("N"));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        foreach (var (kind, role) in roles)
        {
            var login = kind != "owner_role" ? $"LOGIN PASSWORD '{password}'" : "NOLOGIN";
            await using var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" {login} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS", connection);
            await create.ExecuteNonQueryAsync(ct);
        }
        var user = new LocalUser(Guid.NewGuid(), Issuer, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await using (var context = BootstrapTests.Context(database.ConnectionString))
        { context.LocalUsers.Add(user); await context.SaveChangesAsync(ct); }
        await postgres.ApplyPermissionsAsync(database, roles, ct);
        string As(string role) => new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = roles[role], Password = password }.ConnectionString;
        return new(database, roles, As("operator_role"), As("runtime_role"), As("migration_role"), user.Id, user.Subject);
    }

    public string[] Change(string action = "grant", string role = "Author") => ["roles", action, "--user-id", UserId.ToString(), "--role", role, "--reason", "fixture reason 😀"];

    public async Task<ConsoleResult> RunAsync(string[] arguments, CancellationToken ct, string? connection = null, string? issuer = Issuer, int timeoutSeconds = 30,
        IReadOnlyDictionary<string, string>? environment = null, Func<Process, Task>? onStarted = null)
    {
        // Run the console entry point from its own output directory, with its own runtimeconfig/appsettings.
        var root = Path.GetDirectoryName(Path.GetDirectoryName(PublishingFixture.ArtifactPath("unused")))!;
        root = Path.GetDirectoryName(root)!;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(root, "src", "BlogEngine.Operator", "bin", configuration, "net10.0", "BlogEngine.Operator.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("Identity__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Operator__", StringComparison.OrdinalIgnoreCase)).ToArray()) { start.Environment.Remove(key); }
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Identity__Issuer"] = issuer ?? string.Empty;
        start.Environment["Identity__JwksUrl"] = "http://127.0.0.1:1/must-not-be-called";
        start.Environment["ConnectionStrings__Blog"] = connection ?? ConnectionString;
        start.Environment["Operator__TimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (environment is not null) { foreach (var (key, value) in environment) { start.Environment[key] = value; } }
        using var process = Process.Start(start)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            if (onStarted is not null) { await onStarted(process); }
            await process.WaitForExitAsync(ct);
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}

internal sealed record ConsoleResult(int ExitCode, string Stdout, string Stderr)
{
    public JsonElement Success()
    {
        Assert.Equal(0, ExitCode); Assert.Equal(string.Empty, Stderr);
        return JsonDocument.Parse(Stdout).RootElement.Clone();
    }
    public JsonElement Error(int exitCode, string code)
    {
        Assert.Equal(exitCode, ExitCode); Assert.Equal(string.Empty, Stdout);
        var error = JsonDocument.Parse(Stderr).RootElement.Clone();
        Assert.Equal(["code", "message", "correlationId"], error.EnumerateObject().Select(p => p.Name));
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.NotEqual(Guid.Empty, error.GetProperty("correlationId").GetGuid());
        Assert.DoesNotContain("Host=", Stderr, StringComparison.OrdinalIgnoreCase);
        return error;
    }
}
