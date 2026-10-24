using System.Net;
using System.Text;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.ReadModels;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Discussions.ModerationContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Discussions;

[Trait("Category", "Moderation")]
public sealed class ModerationQueryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ActualHttpQueriesAreBoundedProjectedAndExplainedForAllFilterCombinations()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture(); await using var f = await StartAsync(postgres, ct, capture);
        var admin = await f.ActorAsync(LocalRole.Admin, ct); var pub = await PublishedAsync(admin, "moderation-query", ct); var pubId = pub.GetProperty("id").GetGuid();
        var other = await PublishedAsync(admin, "moderation-query-other", ct); var otherId = other.GetProperty("id").GetGuid(); var user = pub.GetProperty("author").GetProperty("id").GetGuid();
        await using var seed = BootstrapTests.Context(f.Database.ConnectionString);
        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO discussions.comments(id,publication_id,author_id,parent_id,depth,state,text,created_at,updated_at,version,visible_descendant_count)
            SELECT md5('moderation-'||n)::uuid,CASE WHEN n%2=0 THEN {pubId} ELSE {otherId} END,{user},NULL,1,(n%3)::smallint,
            CASE WHEN n%3=2 THEN NULL ELSE 'private-query-marker-'||n END,
            '2026-10-24Z'::timestamptz,'2026-10-24Z'::timestamptz,md5('v-moderation-'||n)::uuid,0
            FROM generate_series(6000,1,-1) n
            """, ct);
        await seed.Database.ExecuteSqlRawAsync("ANALYZE discussions.comments; ANALYZE publishing.publications", ct);
        await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct);
        var evidence = new List<object>(); var report = new StringBuilder("# US5 moderation query inspection\n\nPostgreSQL 18.6; 6000 deterministic comments, two Published parents, equal timestamps. Actual runtime-principal HTTP SQL, separate auth/bootstrap commands excluded.\n\n");
        foreach (var filter in new[] { "", "state=Hidden", "publicationId=" + pubId, "publicationId=" + pubId + "&state=Hidden" })
        {
            foreach (var size in new[] { 20, 100 })
            {
                foreach (var number in new[] { 1, int.MaxValue })
                {
                    capture.Clear();
                    var page = await JsonAsync(await admin.GetAsync($"{ListPath}?pageSize={size}&page={number}" + (filter == "" ? "" : "&" + filter), ct), HttpStatusCode.OK, ct);
                    var data = capture.Commands.Where(c => !c.IsAuthentication).ToArray();
                    Assert.Equal(filter.Contains("publicationId", StringComparison.Ordinal) ? 2 : 1, data.Length);
                    Assert.Equal(3, capture.Commands.Count(c => c.IsAuthentication));
                    var command = Assert.Single(data, c => c.Sql.Contains("OFFSET", StringComparison.Ordinal));
                    Assert.DoesNotContain("visible_descendant_count", command.Sql, StringComparison.Ordinal); Assert.DoesNotContain("updated_at", command.Sql, StringComparison.Ordinal);
                    Assert.DoesNotContain("markdown", command.Sql, StringComparison.Ordinal); Assert.Contains("p.state=1", command.Sql, StringComparison.Ordinal);
                    await using var replay = new NpgsqlCommand(command.Sql, connection);
                    foreach (var parameter in command.Parameters) { replay.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value }); }
                    var ids = new List<Guid>();
                    await using (var reader = await replay.ExecuteReaderAsync(ct))
                    { while (await reader.ReadAsync(ct)) { ids.Add(reader.GetGuid(reader.GetOrdinal("Id"))); } }
                    Assert.InRange(ids.Count, 0, size + 1); Assert.Equal(ids.Take(size), page.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
                    Assert.Equal(ids.Count > size, page.GetProperty("hasNextPage").GetBoolean());
                    replay.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.Sql;
                    var planText = (string)(await replay.ExecuteScalarAsync(ct))!; var plan = JsonSerializer.Deserialize<JsonElement>(planText);
                    evidence.Add(new { filter, page = number, pageSize = size, rows = ids.Count, dataQueries = data.Length, sql = command.Sql, parameters = command.Parameters.Select(p => new { p.ParameterName, type = p.NpgsqlDbType.ToString(), p.Value }), plan });
                    report.AppendLine($"## Filter `{(filter == "" ? "none" : filter)}`, page {number}, size {size}\n\nData queries: {data.Length}; returned rows: {ids.Count} (limit {size + 1}).\n\n```sql\n{command.Sql}\n```\n\n```json\n{planText}\n```\n");
                }
            }
        }
        await using var scope = f.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<ModerationQuery>();
        await query.ListAsync(new CurrentActor(user, [LocalRole.Admin]), pubId, null, new PageRequest(), ct); Assert.Empty(db.ChangeTracker.Entries());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ListAsync(new CurrentActor(user, [LocalRole.Admin]), null, null, new PageRequest(), cancellation.Token));
        await File.WriteAllTextAsync(ArtifactPath("queries/us5-moderation.json"), JsonSerializer.Serialize(evidence), ct);
        report.AppendLine("## Index decision\n\nExisting ix_comments_moderation (created_at DESC,id DESC) and ix_comments_publication_state (publication_id,state,created_at DESC,id DESC) cover the planned orders. Plans are retained above for each filter; bounded first pages and scans for distant empty pages do not justify a new migration on this dataset. SC-005 remains in the final phase.\n\nPublic privacy is verified by ModerationContractTests and the US4 tree regressions; captured logs omit retained text. No schema or DB grant changes.\n");
        await File.WriteAllTextAsync(ArtifactPath("us5-queries.md"), report.ToString(), ct);
    }
}
