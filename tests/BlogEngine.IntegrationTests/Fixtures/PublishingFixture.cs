using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Accounts.Domain;
using BlogEngine.IntegrationTests.Accounts;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace BlogEngine.IntegrationTests.Fixtures;

internal sealed class PublishingFixture : IAsyncDisposable
{
    public static string ArtifactPath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "BlogEngine.slnx")))
        { directory = directory.Parent ?? throw new InvalidOperationException("Repository root was not found."); }
        var path = Path.Combine(directory.FullName, "artifacts", "validation", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
    private readonly List<HttpClient> clients = [];
    private PublishingFixture(PostgresDatabase database, JwksFixture identity, ApiFactory factory, Dictionary<string, string> roles)
    { Database = database; Identity = identity; Factory = factory; Roles = roles; Anonymous = Client(); }
    public PostgresDatabase Database { get; }
    public JwksFixture Identity { get; }
    public ApiFactory Factory { get; }
    public Dictionary<string, string> Roles { get; }
    public HttpClient Anonymous { get; }

    public static async Task<PublishingFixture> StartAsync(PostgresFixture postgres, CancellationToken ct, DbCommandInterceptor? commandInterceptor = null)
    {
        var database = await postgres.CreateDatabaseAsync(BootstrapTests.Context, ct);
        var roles = new[] { "owner_role", "migration_role", "runtime_role", "operator_role" }.ToDictionary(x => x, _ => "pub_" + Guid.NewGuid().ToString("N"));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        foreach (var (kind, role) in roles)
        {
            var login = kind == "runtime_role" ? $"LOGIN PASSWORD '{password}'" : "NOLOGIN";
            await using var command = new NpgsqlCommand($"CREATE ROLE \"{role}\" {login} NOSUPERUSER NOCREATEDB NOCREATEROLE", connection);
            await command.ExecuteNonQueryAsync(ct);
        }
        await postgres.ApplyPermissionsAsync(database, roles, ct);
        var runtime = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = roles["runtime_role"], Password = password }.ConnectionString;
        var identity = new JwksFixture();
        await identity.InitializeAsync();
        return new(database, identity, new ApiFactory(runtime, identity, commandInterceptor), roles);
    }
    private HttpClient Client() { var client = Factory.CreateClient(); clients.Add(client); return client; }
    public async Task<HttpClient> ActorAsync(LocalRole role, CancellationToken ct)
    {
        var subject = Guid.NewGuid(); var id = Guid.NewGuid();
        await using var db = BootstrapTests.Context(Database.ConnectionString);
        db.LocalUsers.Add(new LocalUser(id, Identity.Issuer, subject, DateTimeOffset.UtcNow));
        if (role != LocalRole.Reader) { db.RoleAssignments.Add(new RoleAssignment(id, role, DateTimeOffset.UtcNow)); }
        await db.SaveChangesAsync(ct);
        var client = Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", Identity.CreateToken(subject));
        return client;
    }
    public static object Content(string slug = "hello", string markdown = "# untrusted <script>alert(1)</script>", Guid[]? categories = null, Guid[]? tags = null) =>
        new { title = " Title ", slug, markdown, categoryIds = categories ?? [], tagIds = tags ?? [] };
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        using (response) { Assert.Equal(expected, response.StatusCode); return await response.Content.ReadFromJsonAsync<JsonElement>(ct); }
    }
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, Guid? version, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (version is not null) { request.Headers.Add("X-Resource-Version", version.Value.ToString("D")); }
        if (body is not null) { request.Content = JsonContent.Create(body); }
        return await client.SendAsync(request, ct);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients) { client.Dispose(); }
        await Factory.DisposeAsync(); await Identity.DisposeAsync(); await Database.DisposeAsync();
    }
}
