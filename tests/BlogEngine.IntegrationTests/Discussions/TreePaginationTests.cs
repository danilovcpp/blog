using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Reading;
using Microsoft.EntityFrameworkCore;
using static BlogEngine.IntegrationTests.Discussions.DiscussionContractTests;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Discussions;

[Trait("Category", "Discussions")]
[Trait("Category", "ConsumerContract")]
public sealed class TreePaginationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task All250RootsAnd250RepliesEachAreBoundedOrderedAndReachableThroughPlaceholders()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture(); await using var f = await StartAsync(postgres, ct, capture);
        var author = await f.ActorAsync(LocalRole.Author, ct); var pub = await PublishedAsync(author, "tree", ct); var pubId = pub.GetProperty("id").GetGuid(); var user = pub.GetProperty("author").GetProperty("id").GetGuid();
        await using var db = BootstrapTests.Context(f.Database.ConnectionString);
        // Deterministic UUIDs, equal timestamps and deliberately reversed insertion order exercise UUID tie breaking.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO discussions.comments(id,publication_id,author_id,parent_id,depth,state,text,created_at,updated_at,version,visible_descendant_count)
            SELECT md5('root-'||n)::uuid,{pubId},{user},NULL,1,0,'root','2026-10-24Z'::timestamptz,'2026-10-24Z'::timestamptz,md5('version-root-'||n)::uuid,250
            FROM generate_series(250,1,-1) n;
            INSERT INTO discussions.comments(id,publication_id,author_id,parent_id,depth,state,text,created_at,updated_at,version,visible_descendant_count)
            SELECT md5('child-'||n||'-'||r)::uuid,{pubId},{user},md5('root-'||n)::uuid,2,0,'reply','2026-10-24Z'::timestamptz,'2026-10-24Z'::timestamptz,md5('v-child-'||n||'-'||r)::uuid,0
            FROM generate_series(250,1,-1) n CROSS JOIN generate_series(250,1,-1) r
            """, ct);
        var rootIds = await db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM discussions.comments WHERE parent_id IS NULL ORDER BY created_at,id").ToArrayAsync(ct);
        async Task<Guid[]> Walk(Guid? parent)
        {
            var ids = new List<Guid>();
            for (var page = 1; ; page++)
            {
                using var response = await f.Anonymous.GetAsync($"/api/v1/publications/{pubId}/comments?page={page}&pageSize=100" + (parent is null ? "" : "&parentId=" + parent), ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                var items = data.GetProperty("items").EnumerateArray().ToArray(); Assert.InRange(items.Length, 1, 100);
                Assert.All(items, x => Assert.Equal(parent is null ? 1 : 2, x.GetProperty("depth").GetInt32()));
                ids.AddRange(items.Select(x => x.GetProperty("id").GetGuid()));
                if (!data.GetProperty("hasNextPage").GetBoolean()) { break; }
            }
            Assert.Equal(250, ids.Count); Assert.Equal(250, ids.Distinct().Count()); return ids.ToArray();
        }
        Assert.Equal(rootIds, await Walk(null));
        foreach (var root in rootIds)
        {
            var expected = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM discussions.comments WHERE parent_id={root} ORDER BY created_at,id").ToArrayAsync(ct);
            Assert.Equal(expected, await Walk(root));
        }
        var empty = await JsonAsync(await f.Anonymous.GetAsync($"/api/v1/publications/{pubId}/comments?page={int.MaxValue}&pageSize=100", ct), HttpStatusCode.OK, ct);
        Assert.Empty(empty.GetProperty("items").EnumerateArray()); Assert.False(empty.GetProperty("hasNextPage").GetBoolean());
        // Add levels three through five, then turn levels one through four into placeholders.
        var branch = await CreateAsync(author, pubId, rootIds[0], ct); var ancestors = new List<Guid> { rootIds[0], branch.GetProperty("id").GetGuid() };
        for (var depth = 3; depth <= 5; depth++) { branch = await CreateAsync(author, pubId, branch.GetProperty("id").GetGuid(), ct); if (depth < 5) { ancestors.Add(branch.GetProperty("id").GetGuid()); } }
        foreach (var ancestor in ancestors)
        {
            var context = await JsonAsync(await author.GetAsync($"/api/v1/comments/{ancestor}", ct), HttpStatusCode.OK, ct);
            using var deleted = await SendAsync(author, HttpMethod.Delete, $"/api/v1/comments/{ancestor}", context.GetProperty("version").GetGuid(), null, ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        foreach (var ancestor in ancestors)
        {
            var children = await JsonAsync(await f.Anonymous.GetAsync($"/api/v1/publications/{pubId}/comments?parentId={ancestor}", ct), HttpStatusCode.OK, ct);
            Assert.NotEmpty(children.GetProperty("items").EnumerateArray());
            Assert.All(children.GetProperty("items").EnumerateArray().Where(x => x.GetProperty("state").GetString() == "Deleted"), x =>
            { Assert.Equal(JsonValueKind.Null, x.GetProperty("content").ValueKind); Assert.Equal(JsonValueKind.Null, x.GetProperty("author").ValueKind); });
        }
        await CommentConcurrencyTests.AssertCountsAsync(db, ct);
        await db.Database.ExecuteSqlRawAsync("ANALYZE discussions.comments", ct);
        await using var connection = new Npgsql.NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync(ct);
        var evidence = new List<object>();
        foreach (var parent in new Guid?[] { null, rootIds[0], ancestors[^1] })
        {
            foreach (var size in new[] { 20, 100 })
            {
                foreach (var number in new[] { 1, 3, int.MaxValue })
                {
                    capture.Clear();
                    var page = await JsonAsync(await f.Anonymous.GetAsync($"/api/v1/publications/{pubId}/comments?page={number}&pageSize={size}" + (parent is null ? "" : "&parentId=" + parent), ct), HttpStatusCode.OK, ct);
                    var commands = capture.Commands; Assert.Equal(parent is null ? 2 : 3, commands.Count);
                    var data = Assert.Single(commands, c => c.Sql.Contains("OFFSET", StringComparison.Ordinal));
                    Assert.DoesNotContain("WITH RECURSIVE", data.Sql, StringComparison.OrdinalIgnoreCase); Assert.Contains("EXISTS", data.Sql, StringComparison.Ordinal);
                    Assert.DoesNotContain("c.version", data.Sql, StringComparison.Ordinal); Assert.Contains("CASE", data.Sql, StringComparison.Ordinal);
                    await using var replay = new Npgsql.NpgsqlCommand(data.Sql, connection);
                    foreach (var parameter in data.Parameters) { replay.Parameters.Add(new Npgsql.NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value }); }
                    var materialized = 0; await using (var reader = await replay.ExecuteReaderAsync(ct)) { while (await reader.ReadAsync(ct)) { materialized++; } }
                    Assert.InRange(materialized, 0, size + 1); Assert.InRange(page.GetProperty("items").GetArrayLength(), 0, size);
                    replay.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + data.Sql;
                    var plan = JsonSerializer.Deserialize<JsonElement>((string)(await replay.ExecuteScalarAsync(ct))!);
                    evidence.Add(new
                    {
                        parent,
                        page = number,
                        pageSize = size,
                        queries = commands.Count,
                        materialized,
                        sql = data.Sql,
                        parameters = data.Parameters.Select(p => new { p.ParameterName, type = p.NpgsqlDbType.ToString(), value = p.Value is DBNull ? null : p.Value }),
                        plan
                    });
                }
            }
        }
        await File.WriteAllTextAsync(ArtifactPath("queries/us4-tree.json"), JsonSerializer.Serialize(evidence), ct);
    }
}
