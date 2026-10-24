using System.Net;
using System.Text.Json;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.Performance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Reading;

[Trait("Category", "Reading")]
[Trait("Category", "BenchmarkDataset")]
public sealed class FullDatasetQueryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact(Timeout = 1_200_000)]
    public async Task FullSeedReproducesCanonicalChecksumsAndActualSqlHasBoundedPlansIncludingTaxonomyClone()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture();
        await using var f = await StartAsync(postgres, ct, capture, benchmark: true);
        var seed = new SeedDataset(f.Identity.Issuer); var output = ArtifactPath("../../artifacts/performance/sc-005/preparation");
        var databaseName = new NpgsqlConnectionStringBuilder(f.Database.ConnectionString).Database!;
        var manifest = await seed.SeedAsync(f.Database.ConnectionString, databaseName, output, ct, PostgresImage.Reference);
        var repeated = await new SeedDataset(f.Identity.Issuer).SeedAsync(f.Database.ConnectionString, databaseName, output, ct, PostgresImage.Reference);
        Assert.Equal(manifest.Tables.OrderBy(p => p.Key), repeated.Tables.OrderBy(p => p.Key));
        var plan = new RequestPlan(seed);
        await plan.SaveAsync(output, ct);
        var scenarios = new List<object>();
        await using (var connection = new NpgsqlConnection(f.Database.ConnectionString))
        {
            await connection.OpenAsync(ct);
            for (var slot = 0; slot < 400; slot++)
            {
                var request = plan.Get(0, slot);
                f.Anonymous.DefaultRequestHeaders.Authorization = request.Template.Bearer ? new("Bearer", f.Identity.CreateToken(SeedDataset.Id("subject", request.User))) : null;
                capture.Clear(); var json = await JsonAsync(await f.Anonymous.GetAsync(request.Path, ct), HttpStatusCode.OK, ct); plan.ValidateResponse(request, json);
                scenarios.Add(await InspectAsync(connection, capture, $"sc005/profile-{slot:D3}", request.Template.Kind == "feed" ? 5 : 3, request.Template.Size, manifest, ct));
            }
            f.Anonymous.DefaultRequestHeaders.Authorization = null;
            var filters = new List<(string Name, int[] Categories, int[] Tags)> { ("none", [], []) };
            foreach (var (tier, category, tag) in new[] { ("p", 0, 0), ("m", 10, 20), ("r", 50, 200) })
            {
                filters.Add(($"category-{tier}-1", [category], [])); filters.Add(($"category-{tier}-5", Enumerable.Range(category, 5).ToArray(), []));
                filters.Add(($"tag-{tier}-1", [], [tag])); filters.Add(($"tag-{tier}-20", [], Enumerable.Range(tag, 20).ToArray()));
                filters.Add(($"combined-{tier}-1", [category], [tag])); filters.Add(($"combined-{tier}-max", Enumerable.Range(category, 5).ToArray(), Enumerable.Range(tag, 20).ToArray()));
            }
            foreach (var sort in new[] { "recent", "popular" })
            {
                foreach (var size in new[] { 20, 100 })
                {
                    foreach (var page in new[] { 1, 10, 100, 1000 })
                    {
                        foreach (var filter in filters)
                        {
                            var path = $"/api/v1/publications?sort={sort}&page={page}&pageSize={size}"
                                + string.Concat(filter.Categories.Select(c => "&categoryIds=" + SeedDataset.Id("categories", c)))
                                + string.Concat(filter.Tags.Select(t => "&tagIds=" + SeedDataset.Id("tags", t)));
                            capture.Clear(); var json = await JsonAsync(await f.Anonymous.GetAsync(path, ct), HttpStatusCode.OK, ct);
                            Assert.InRange(json.GetProperty("items").GetArrayLength(), 0, size);
                            plan.ValidateResponse(plan.ExpectedFeed(path, sort, page, size, filter.Categories, filter.Tags), json);
                            if (filter.Tags.Length == 20) { Assert.Empty(json.GetProperty("items").EnumerateArray()); }
                            scenarios.Add(await InspectAsync(connection, capture, $"sc005/{sort}-{size}-{page}-{filter.Name}", 5, size, manifest, ct));
                        }
                    }
                }
            }
            // Elevation is confined to query inspection and reverted before verifying the canonical dataset again.
            await using (var elevate = new NpgsqlCommand("INSERT INTO accounts.role_assignments(user_id,role,granted_at) VALUES (@id,3,@at)", connection))
            { elevate.Parameters.AddWithValue("id", SeedDataset.Id("user", 0)); elevate.Parameters.AddWithValue("at", SeedDataset.BaseTime); await elevate.ExecuteNonQueryAsync(ct); }
            f.Anonymous.DefaultRequestHeaders.Authorization = new("Bearer", f.Identity.CreateToken(SeedDataset.Id("subject", 0)));
            var rootId = Guid.Empty; var publicationId = Guid.Empty;
            await using (var roots = new NpgsqlCommand("SELECT id,publication_id FROM discussions.comments WHERE parent_id IS NULL AND (state=0 OR visible_descendant_count>0) ORDER BY id LIMIT 1", connection))
            { await using var reader = await roots.ExecuteReaderAsync(ct); Assert.True(await reader.ReadAsync(ct)); rootId = reader.GetGuid(0); publicationId = reader.GetGuid(1); }
            foreach (var size in new[] { 20, 100 })
            {
                foreach (var page in new[] { 1, 10, 100, 1000 })
                {
                    foreach (var (name, path) in new[]
                    {
                ("workspace", "/api/v1/workspace/publications"), ("moderation", "/api/v1/moderation/comments"),
                ("roots", $"/api/v1/publications/{publicationId}/comments"), ("replies", $"/api/v1/publications/{publicationId}/comments?parentId={rootId}"),
            })
                    {
                        capture.Clear(); var json = await JsonAsync(await f.Anonymous.GetAsync(path + (path.Contains('?') ? "&" : "?") + $"page={page}&pageSize={size}", ct), HttpStatusCode.OK, ct);
                        Assert.InRange(json.GetProperty("items").GetArrayLength(), 0, size);
                        scenarios.Add(await InspectAsync(connection, capture, $"sc005/{name}-{size}-{page}", name is "replies" or "workspace" ? 3 : 2, size, manifest, ct));
                    }
                }
            }

            await using (var revoke = new NpgsqlCommand("DELETE FROM accounts.role_assignments WHERE user_id=@id AND role=3", connection))
            { revoke.Parameters.AddWithValue("id", SeedDataset.Id("user", 0)); await revoke.ExecuteNonQueryAsync(ct); }
            f.Anonymous.DefaultRequestHeaders.Authorization = null;
            await TaxonomyAsync(f.Anonymous, capture, connection, f.Factory.Services, "baseline", manifest, ct);
        }
        await new SeedDataset(f.Identity.Issuer).SeedAsync(f.Database.ConnectionString, databaseName, output, ct, PostgresImage.Reference);
        await using (var benchmarkIdentity = new BenchmarkIdentityFixture(f.Identity.Issuer, f.Identity.Audience))
        {
            await benchmarkIdentity.StartAsync("http://127.0.0.1:0", new Uri("http://127.0.0.1/benchmark-jwks.json"), ct);
            f.Identity.Time.Advance(DateTimeOffset.UtcNow - f.Identity.Time.GetUtcNow());
            await using var benchmarkApi = f.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Identity:JwksUrl"] = benchmarkIdentity.LocalJwksUrl!.AbsoluteUri })));
            using var initialize = benchmarkApi.CreateClient();
            var diagnostic = new ReadBenchmark(plan, benchmarkIdentity, new Uri("http://localhost"), benchmarkApi.Server.CreateHandler());
            Assert.False(await diagnostic.RunAsync(Path.Combine(output, "diagnostic"), diagnostic: true, ct));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "diagnostic", "read-report.json"), ct));
            Assert.True(report.RootElement.GetProperty("responseContractPassed").GetBoolean());
            // Unsuitable local hardware may time out; payload/cache errors always fail separately.
            Assert.InRange(report.RootElement.GetProperty("total").GetProperty("failures").GetInt32(), 0, report.RootElement.GetProperty("total").GetProperty("attempts").GetInt32());
            Assert.True(report.RootElement.GetProperty("completeBlocks").GetBoolean());
        }
        string runtimeConnection;
        await using (var scope = f.Factory.Services.CreateAsyncScope())
        { runtimeConnection = scope.ServiceProvider.GetRequiredService<BlogDbContext>().Database.GetConnectionString()!; }
        await f.Factory.DisposeAsync(); using (var pool = new NpgsqlConnection(runtimeConnection)) { NpgsqlConnection.ClearPool(pool); }
        await using var clone = await postgres.CloneDatabaseAsync(f.Database, ct);
        var cloneConnection = new NpgsqlConnectionStringBuilder(runtimeConnection) { Database = new NpgsqlConnectionStringBuilder(clone.ConnectionString).Database }.ConnectionString;
        var cloneCapture = new ReadingSqlCapture(); await using var cloneApi = new ApiFactory(cloneConnection, f.Identity, cloneCapture); using var cloneClient = cloneApi.CreateClient();
        await using (var connection = new NpgsqlConnection(clone.ConnectionString))
        {
            await connection.OpenAsync(ct);
            foreach (var (table, first, last) in new[] { ("categories", 200, 2000), ("tags", 2000, 20000) })
            {
                await using var writer = await connection.BeginBinaryImportAsync($"COPY publishing.{table}(id,name,slug,version) FROM STDIN (FORMAT BINARY)", ct);
                for (var n = first; n < last; n++)
                { await writer.StartRowAsync(ct); await writer.WriteAsync(SeedDataset.Id(table, n), ct); await writer.WriteAsync("Topic " + (n / 2).ToString("D5", System.Globalization.CultureInfo.InvariantCulture), ct); await writer.WriteAsync(table + "-" + n, ct); await writer.WriteAsync(SeedDataset.Id(table + "-version", n), ct); }
                await writer.CompleteAsync(ct);
            }
            await using (var analyze = new NpgsqlCommand("ANALYZE publishing.categories; ANALYZE publishing.tags", connection)) { await analyze.ExecuteNonQueryAsync(ct); }
            await TaxonomyAsync(cloneClient, cloneCapture, connection, cloneApi.Services, "expanded-copy", manifest, ct);
        }
        var queryRoot = ArtifactPath("queries");
        var planFiles = Directory.EnumerateFiles(Path.Combine(queryRoot, "sc005"), "*.json").Concat(Directory.EnumerateFiles(Path.Combine(queryRoot, "taxonomy"), "*.json"))
            .Order(StringComparer.Ordinal).Select(file => new { path = Path.GetRelativePath(queryRoot, file).Replace('\\', '/'), sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) }).ToArray();
        await File.WriteAllTextAsync(ArtifactPath("queries/sc005-summary.json"), JsonSerializer.Serialize(new
        {
            status = "passed",
            schemaSha256 = manifest.SchemaSha256,
            dataChecksums = manifest.Tables,
            revision = manifest.Environment.GetProperty("revision"),
            postgresSettings = manifest.Environment.GetProperty("postgres"),
            plan.CatalogChecksum,
            plan.TemplateChecksum,
            scenarios,
            planFiles,
            note = "Query correctness and plans on full seed; local hardware does not establish SC-005 latency acceptance."
        }, BenchmarkManifest.Json), ct);
    }

    private static async Task<object> InspectAsync(NpgsqlConnection connection, ReadingSqlCapture capture, string name, int maximumQueries, int pageSize, BenchmarkManifest manifest, CancellationToken ct)
    {
        var commands = capture.Commands; var data = commands.Where(c => !c.IsAuthentication).ToArray(); Assert.InRange(data.Length, 1, maximumQueries);
        var evidence = new List<object>();
        foreach (var recorded in data)
        {
            await using var replay = QueryInspectionTests.Command(connection, recorded); var rows = 0; string[] columns;
            await using (var reader = await replay.ExecuteReaderAsync(ct))
            { columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray(); while (await reader.ReadAsync(ct)) { rows++; } }
            var list = recorded.Sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase);
            if (list && pageSize > 0) { Assert.InRange(rows, 0, pageSize + 1); }
            if (name.Contains("profile-", StringComparison.Ordinal) && pageSize > 0 && recorded.Sql.Contains("FROM publishing.publications", StringComparison.Ordinal))
            { Assert.DoesNotContain("markdown", recorded.Sql, StringComparison.OrdinalIgnoreCase); }
            await using var explain = QueryInspectionTests.Command(connection, recorded, "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) ");
            var plan = JsonSerializer.Deserialize<JsonElement>((string)(await explain.ExecuteScalarAsync(ct))!);
            evidence.Add(new { recorded.Sql, parameters = recorded.Parameters.Select(p => new { p.ParameterName, type = QueryInspectionTests.ParameterType(p), value = p.Value is DBNull ? null : p.Value }), rows, columns, plan });
        }
        var record = new { name, status = "passed", manifest.SchemaSha256, dataChecksums = manifest.Tables, dataQueries = data.Length, authenticationQueries = commands.Count(c => c.IsAuthentication), commands = evidence };
        await File.WriteAllTextAsync(ArtifactPath("queries/" + name + ".json"), JsonSerializer.Serialize(record), ct); return new { name, dataQueries = data.Length };
    }

    private static async Task TaxonomyAsync(HttpClient client, ReadingSqlCapture capture, NpgsqlConnection connection, IServiceProvider services, string volume, BenchmarkManifest manifest, CancellationToken ct)
    {
        foreach (var kind in new[] { "categories", "tags" })
        {
            await using (var scope = services.CreateAsyncScope())
            {
                var query = scope.ServiceProvider.GetRequiredService<BlogEngine.Infrastructure.ReadModels.TaxonomyQueries>();
                var page = await query.ListAsync(kind == "categories" ? BlogEngine.Core.Publishing.Contracts.TaxonomyKind.Category : BlogEngine.Core.Publishing.Contracts.TaxonomyKind.Tag, new(1, 100), ct);
                Assert.Equal(100, page.Items.Count);
                Assert.Empty(scope.ServiceProvider.GetRequiredService<BlogDbContext>().ChangeTracker.Entries());
            }
            var expected = new List<Guid>();
            await using (var all = new NpgsqlCommand($"SELECT id FROM publishing.{kind} ORDER BY name COLLATE \"C\",id", connection))
            { await using var reader = await all.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) { expected.Add(reader.GetGuid(0)); } }
            foreach (var size in new[] { 0, 20, 100 })
            {
                var effective = size == 0 ? 20 : size; var last = (expected.Count + effective - 1) / effective;
                foreach (var page in new[] { 1, Math.Max(1, last / 2), last, last + 1 })
                {
                    var path = $"/api/v1/{kind}?page={page}" + (size == 0 ? "" : "&pageSize=" + size);
                    capture.Clear(); var json = await JsonAsync(await client.GetAsync(path, ct), HttpStatusCode.OK, ct);
                    var items = json.GetProperty("items").EnumerateArray().ToArray(); Assert.Equal(expected.Skip((page - 1) * effective).Take(effective), items.Select(i => i.GetProperty("id").GetGuid()));
                    Assert.Equal(expected.Count > page * effective, json.GetProperty("hasNextPage").GetBoolean());
                    Assert.All(items, item => Assert.Equal(new[] { "id", "name", "slug", "version" }, item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
                    var command = Assert.Single(capture.Commands, c => !c.IsAuthentication);
                    Assert.Contains("COLLATE \"C\"", command.Sql, StringComparison.Ordinal); Assert.Contains("LIMIT", command.Sql, StringComparison.Ordinal); Assert.Contains("OFFSET", command.Sql, StringComparison.Ordinal);
                    Assert.DoesNotContain("COUNT(", command.Sql, StringComparison.OrdinalIgnoreCase);
                    await using (var replay = QueryInspectionTests.Command(connection, command))
                    {
                        await using var reader = await replay.ExecuteReaderAsync(ct); Assert.Equal(4, reader.FieldCount); var rows = 0;
                        while (await reader.ReadAsync(ct)) { rows++; }
                        Assert.InRange(rows, 0, effective + 1);
                    }
                    await InspectAsync(connection, capture, $"taxonomy/{volume}-{kind}-{size}-{page}", 1, effective, manifest, ct);
                }
            }
            var seen = new List<Guid>();
            for (var page = 1; seen.Count < expected.Count; page++)
            { var json = await JsonAsync(await client.GetAsync($"/api/v1/{kind}?page={page}&pageSize=100", ct), HttpStatusCode.OK, ct); seen.AddRange(json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())); }
            Assert.Equal(expected, seen); Assert.Equal(expected.Count, seen.Distinct().Count());
            await using var indexes = new NpgsqlCommand($"SELECT indexdef FROM pg_indexes WHERE schemaname='publishing' AND tablename='{kind}' ORDER BY indexname", connection);
            var definitions = new List<string>(); await using (var reader = await indexes.ExecuteReaderAsync(ct)) { while (await reader.ReadAsync(ct)) { definitions.Add(reader.GetString(0)); } }
            Assert.Contains(definitions, i => i.Contains("(name, id)", StringComparison.Ordinal));
            await using (var collation = new NpgsqlCommand("SELECT c.collname FROM pg_index i JOIN pg_class x ON x.oid=i.indexrelid JOIN pg_collation c ON c.oid=i.indcollation[0] WHERE x.relname=@name", connection))
            { collation.Parameters.AddWithValue("name", "ix_" + kind + "_name_id"); Assert.Equal("C", await collation.ExecuteScalarAsync(ct)); }
            await File.WriteAllTextAsync(ArtifactPath($"queries/taxonomy/{volume}-{kind}-indexes.json"), JsonSerializer.Serialize(new { volume, count = expected.Count, definitions, collation = "C; name column and database use C", assessment = "Index (name,id) matches server ordering. Actual EXPLAIN includes rows/loops/buffers/sort/temp IO/time. Sequential scans on small relations or deep offsets are valid planner choices; no planner switches were changed.", passed = true }), ct);
        }
    }
}
