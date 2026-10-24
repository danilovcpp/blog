using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BlogEngine.Performance;

public sealed record TableChecksum(long Rows, string Sha256);
public sealed record BenchmarkManifest(string Profile, int Seed, string Generator, string CorpusSha256, string SchemaSha256,
    string Issuer, IReadOnlyDictionary<string, TableChecksum> Tables, JsonElement Environment, JsonElement Distribution)
{
    public static JsonSerializerOptions Json { get; } = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.MakeReadOnly(populateMissingResolver: true); return options;
    }
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Schema(BlogDbContext db) => Hash(db.GetService<IMigrator>().GenerateScript().ReplaceLineEndings("\n"));
    public static async Task<JsonElement> EnvironmentAsync(NpgsqlConnection connection, CancellationToken ct, string postgresImage)
    {
        await using var command = new NpgsqlCommand("SELECT name,setting FROM pg_settings WHERE name IN ('server_version','server_encoding','TimeZone','lc_collate','shared_buffers','work_mem','max_connections','effective_cache_size','random_page_cost') ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(ct); var settings = new SortedDictionary<string, string>();
        while (await reader.ReadAsync(ct)) { settings.Add(reader.GetString(0), reader.GetString(1)); }
        return JsonSerializer.SerializeToElement(new
        {
            revision = await RevisionAsync(ct),
            workingTreeDirty = !string.IsNullOrEmpty(await ProcessOutputAsync("git", "status --porcelain --untracked-files=normal", ct)),
            sourceSha256 = await SourceChecksumAsync(ct),
            sdk = await ProcessOutputAsync("dotnet", "--version", ct),
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            logicalProcessors = System.Environment.ProcessorCount,
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            postgres = settings,
            postgresImage,
        }, Json);
    }
    public static Task<string> RevisionAsync(CancellationToken ct) => ProcessOutputAsync("git", "rev-parse HEAD", ct);
    private static async Task<string> SourceChecksumAsync(CancellationToken ct)
    {
        var root = await ProcessOutputAsync("git", "rev-parse --show-toplevel", ct);
        var files = new[] { "src", "tests" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")
                && (Path.GetExtension(path) is ".cs" or ".csproj" || Path.GetFileName(path) == "packages.lock.json"))
            .Concat(new[] { "Directory.Build.props", "Directory.Packages.props", "global.json", "specs/001-blog-engine-mvp/contracts/openapi.yaml" }.Select(path => Path.Combine(root, path)))
            .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal);
        var canonical = new StringBuilder();
        foreach (var file in files)
        { canonical.Append(Path.GetRelativePath(root, file).Replace('\\', '/')).Append('|').Append(Hash((await File.ReadAllTextAsync(file, ct)).ReplaceLineEndings("\n"))).Append('\n'); }
        return Hash(canonical.ToString());
    }
    private static async Task<string> ProcessOutputAsync(string executable, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("Cannot inspect benchmark toolchain.");
        var output = await process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct); await error;
        if (process.ExitCode != 0) { throw new InvalidOperationException("Cannot inspect benchmark toolchain."); }
        return output.Trim();
    }
}
