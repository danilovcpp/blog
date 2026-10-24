using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BlogEngine.Performance;

public sealed class SeedDataset
{
    public const string Profile = "sc005-v1";
    public const int Seed = 20261024;
    public const int PublicationCount = 100_000;
    public static readonly DateTime BaseTime = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    // Versioned ASCII corpus: scalar boundaries and byte padding are unambiguous across runtimes.
    public const string Corpus = "publication reader author discussion storage transaction boundary identity contract reliable invariant data query index page response content language design evidence system independent deterministic review execution record state visible hidden deleted category topic source markdown paragraph list practical precise stable shared ordered version clock window resource counter history current public local platform test measure explain inspect maintain validate protect persist compare build release";
    private static readonly string[] Words = Corpus.Split(' ');
    private readonly Dictionary<string, int[]> ranks = new(StringComparer.Ordinal);
    private readonly int[] commentRanks;
    private readonly int[] publicationOrder;
    private readonly string issuer;

    public SeedDataset(string issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer)) { throw new ArgumentException("A fixture issuer is required.", nameof(issuer)); }
        this.issuer = issuer;
        foreach (var name in new[] { "authors", "sizes", "cat-hot", "cat-secondary", "tag-hot", "tag-secondary", "likes", "comments", "dates" })
        { ranks.Add(name, PermutationRanks(name, PublicationCount)); }
        commentRanks = PermutationRanks("comment-state", 1_000_000);
        publicationOrder = Enumerable.Range(0, PublicationCount).OrderBy(n => Id("publication", n)).ToArray();
    }

    public static byte[] H(string name, int n, int counter = 0) => SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"{Profile}|{Seed}|{name}|{n}|{counter}")));
    public static Guid Id(string name, int ordinal)
    {
        var hash = H(name, ordinal); hash[6] = (byte)((hash[6] & 15) | 128); hash[8] = (byte)((hash[8] & 63) | 128);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
    private static int[] PermutationRanks(string name, int count)
    {
        var keys = Enumerable.Range(0, count).Select(n => (N: n, Key: H(name, n))).ToArray();
        Array.Sort(keys, (a, b) => { var result = a.Key.AsSpan().SequenceCompareTo(b.Key); return result != 0 ? result : a.N.CompareTo(b.N); });
        var result = new int[count]; for (var i = 0; i < keys.Length; i++) { result[keys[i].N] = i; }
        return result;
    }
    public int Rank(string name, int n) => ranks[name][n];
    public int BodyBytes(int n) => Rank("sizes", n) switch { < 70_000 => 2048, < 95_000 => 16384, _ => 131072 };
    public static int RelationCount(int rank) => rank switch { < 20_000 => 0, < 80_000 => 5, < 95_000 => 20, _ => 80 };
    public long LikeCount(int n) => RelationCount(Rank("likes", n));
    public DateTime PublishedAt(int n) => BaseTime.AddMinutes(-30 * (Rank("dates", n) / 10));
    public int[] Categories(int n)
    {
        var r = Rank("cat-secondary", n);
        return [Rank("cat-hot", n) % 10, r % 5 < 4 ? 10 + (4 * (r / 5) + r % 5) % 40 : 50 + (r / 5) % 150];
    }
    public int[] Tags(int n)
    {
        var r = Rank("tag-hot", n); var result = new int[5]; result[0] = r % 20; result[1] = (r + 1 + (r / 20) % 19) % 20;
        for (var s = 0; s < 3; s++)
        {
            var j = 3 * Rank("tag-secondary", n) + s;
            result[2 + s] = j % 5 < 3 ? 20 + (3 * (j / 5) + j % 5) % 180 : 200 + (2 * (j / 5) + j % 5 - 3) % 1800;
        }
        return result;
    }
    public bool LikedBy(int n, int user)
    {
        var start = (int)(BinaryPrimitives.ReadUInt32BigEndian(H("like-user", n)) % 10_000);
        return (user - start + 10_000) % 10_000 < LikeCount(n);
    }
    public static string Body(string name, int n, int bytes)
    {
        var output = new StringBuilder(bytes); output.Append("# Article ").Append(n.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
        for (var counter = 0; output.Length < bytes; counter++)
        {
            if (counter % 4 == 0)
            {
                if (output.Length + 4 > bytes) { output.Append('x', bytes - output.Length); break; }
                output.Append("\n\n- ");
            }
            foreach (var value in H(name, n, counter))
            {
                var word = Words[value % Words.Length];
                if (output.Length + word.Length + 1 > bytes) { output.Append('x', bytes - output.Length); break; }
                output.Append(word).Append(' ');
            }
        }
        // Prefixes above may cross the target at a paragraph boundary. ASCII truncation preserves scalars.
        output.Length = bytes; return output.ToString();
    }

    internal sealed record Table(string Name, string Columns, string PrimaryKey, NpgsqlDbType[] Types, Func<bool, IEnumerable<object?[]>> Rows);
    private IEnumerable<Table> Tables()
    {
        static IEnumerable<int> Ordered(string entity, int count) => Enumerable.Range(0, count).OrderBy(n => Id(entity, n));
        yield return new("accounts.local_users", "id,issuer,subject,created_at", "id", [NpgsqlDbType.Uuid, NpgsqlDbType.Text, NpgsqlDbType.Uuid, NpgsqlDbType.TimestampTz],
            _ => Ordered("user", 10_000).Select(n => new object?[] { Id("user", n), issuer, Id("subject", n), BaseTime.AddDays(-100).AddMinutes(n) }));
        yield return new("accounts.role_assignments", "user_id,role,granted_at", "user_id,role", [NpgsqlDbType.Uuid, NpgsqlDbType.Smallint, NpgsqlDbType.TimestampTz],
            _ => Ordered("user", 1000).Select(n => new object?[] { Id("user", n), (short)1, BaseTime.AddDays(-99) }));
        foreach (var (name, count) in new[] { ("categories", 200), ("tags", 2000) })
        {
            var entity = name;
            yield return new("publishing." + entity, "id,name,slug,version", "id", [NpgsqlDbType.Uuid, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Uuid],
                _ => Ordered(entity, count).Select(n => new object?[] { Id(entity, n), "Topic " + (n / 2).ToString("D5", CultureInfo.InvariantCulture), entity + "-" + n.ToString(CultureInfo.InvariantCulture), Id(entity + "-version", n) }));
        }
        yield return new("publishing.publications", "id,author_id,title,slug,markdown,description,cover_url,state,created_at,updated_at,first_published_at,deleted_at,version,like_count,view_count", "id",
            [NpgsqlDbType.Uuid, NpgsqlDbType.Uuid, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Smallint, NpgsqlDbType.TimestampTz, NpgsqlDbType.TimestampTz, NpgsqlDbType.TimestampTz, NpgsqlDbType.TimestampTz, NpgsqlDbType.Uuid, NpgsqlDbType.Bigint, NpgsqlDbType.Bigint],
            _ => publicationOrder.Select(n => new object?[] { Id("publication", n), Id("user", Rank("authors", n) % 1000), "Article " + n.ToString(CultureInfo.InvariantCulture), "article-" + n.ToString(CultureInfo.InvariantCulture), Body("body", n, BodyBytes(n)), null, null, (short)1, PublishedAt(n).AddDays(-1), PublishedAt(n), PublishedAt(n), null, Id("publication-version", n), LikeCount(n), 10L + 9 * LikeCount(n) }));
        foreach (var (entity, column) in new[] { ("categories", "category_id"), ("tags", "tag_id") })
        {
            var relation = entity;
            yield return new("publishing.publication_" + relation, "publication_id," + column, "publication_id," + column, [NpgsqlDbType.Uuid, NpgsqlDbType.Uuid],
                _ => publicationOrder.SelectMany(n => (relation == "categories" ? Categories(n) : Tags(n)).Select(t => Id(relation, t)).Order().Select(id => new object?[] { Id("publication", n), id })));
        }
        yield return new("engagement.likes", "publication_id,user_id,created_at", "publication_id,user_id", [NpgsqlDbType.Uuid, NpgsqlDbType.Uuid, NpgsqlDbType.TimestampTz], _ => Likes());
        yield return new("discussions.comments", "id,publication_id,author_id,parent_id,depth,state,text,created_at,updated_at,edited_at,version,visible_descendant_count", "id",
            [NpgsqlDbType.Uuid, NpgsqlDbType.Uuid, NpgsqlDbType.Uuid, NpgsqlDbType.Uuid, NpgsqlDbType.Smallint, NpgsqlDbType.Smallint, NpgsqlDbType.Text, NpgsqlDbType.TimestampTz, NpgsqlDbType.TimestampTz, NpgsqlDbType.TimestampTz, NpgsqlDbType.Uuid, NpgsqlDbType.Bigint], Comments);
        yield return new("accounts.role_change_audit", "id,command_correlation_id,target_user_id,role,action,changed,actor,reason,occurred_at", "id", [], _ => []);
        yield return new("engagement.view_deduplication_records", "publication_id,visitor_kind,visitor_key,last_counted_at", "publication_id,visitor_kind,visitor_key", [], _ => []);
    }
    private IEnumerable<object?[]> Likes()
    {
        foreach (var n in publicationOrder)
        {
            var start = (int)(BinaryPrimitives.ReadUInt32BigEndian(H("like-user", n)) % 10_000);
            foreach (var k in Enumerable.Range(0, (int)LikeCount(n)).OrderBy(k => Id("user", (start + k) % 10_000)))
            { yield return [Id("publication", n), Id("user", (start + k) % 10_000), PublishedAt(n).AddMinutes(k + 1)]; }
        }
    }
    private short CommentState(int ordinal) => commentRanks[ordinal] switch { < 900_000 => 0, < 950_000 => 1, _ => 2 };
    private IEnumerable<object?[]> Comments(bool canonical)
    {
        var comments = new List<(int N, int K, int Ordinal)>(1_000_000);
        for (var n = 0; n < PublicationCount; n++)
        {
            var r = Rank("comments", n);
            var start = r switch { < 20_000 => 0, < 80_000 => (r - 20_000) * 5, < 95_000 => 300_000 + (r - 80_000) * 20, _ => 600_000 + (r - 95_000) * 80 };
            for (var k = 0; k < RelationCount(r); k++) { comments.Add((n, k, start + k)); }
        }
        IEnumerable<(int N, int K, int Ordinal)> ordered = canonical ? comments.OrderBy(c => Id("comment", c.Ordinal)) : comments;
        foreach (var (n, k, ordinal) in ordered)
        {
            var state = CommentState(ordinal); var root = k % 5 == 0;
            var visibleChildren = root ? Enumerable.Range(ordinal + 1, 4).Count(c => CommentState(c) == 0) : 0;
            yield return [Id("comment", ordinal), Id("publication", n), Id("user", ordinal % 10_000), root ? null : Id("comment", ordinal - k % 5), (short)(root ? 1 : 2), state,
                state == 2 ? null : Body("comment-body", ordinal, 512).TrimEnd().PadRight(512, '.'), PublishedAt(n).AddMinutes(k + 1), PublishedAt(n).AddMinutes(k + 1), null, Id("comment-version", ordinal), (long)visibleChildren];
        }
    }
    private static void Append(IncrementalHash hash, object?[] row)
    {
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row)); hash.AppendData("\n"u8);
    }
    private Dictionary<string, TableChecksum> ExpectedChecksums(CancellationToken ct)
    {
        var result = new Dictionary<string, TableChecksum>();
        foreach (var table in Tables())
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long count = 0;
            foreach (var row in table.Rows(true)) { ct.ThrowIfCancellationRequested(); Append(hash, row); count++; }
            result.Add(table.Name, new(count, Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        return result;
    }
    private async Task<Dictionary<string, TableChecksum>> ActualChecksumsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<string, TableChecksum>();
        foreach (var table in Tables())
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long count = 0;
            await using var command = new NpgsqlCommand($"SELECT {table.Columns} FROM {table.Name} ORDER BY {table.PrimaryKey}", connection) { CommandTimeout = 300 };
            await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) { row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i); }
                Append(hash, row); count++;
            }
            result.Add(table.Name, new(count, Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        return result;
    }
    public async Task<BenchmarkManifest> SeedAsync(string connectionString, string disposableDatabase, string output, CancellationToken ct, string postgresImage = "not provided")
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.Database != disposableDatabase || !disposableDatabase.StartsWith("sc005_disposable_", StringComparison.Ordinal))
        { throw new InvalidOperationException("Seed requires the exact explicitly disposable sc005_disposable_ database name."); }
        Directory.CreateDirectory(output);
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(connectionString).Options);
        if (db.Database.HasPendingModelChanges() || (await db.Database.GetPendingMigrationsAsync(ct)).Any())
        { throw new InvalidOperationException("Apply the reviewed migration chain before Seed."); }
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
        await using (var profile = new NpgsqlCommand("SELECT current_setting('server_version'), current_setting('server_encoding'), current_setting('TimeZone'), datcollate FROM pg_database WHERE datname=current_database()", connection))
        {
            await using var reader = await profile.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            if (!reader.GetString(0).StartsWith("18.6", StringComparison.Ordinal) || reader.GetString(1) != "UTF8" || reader.GetString(2) != "UTC" || reader.GetString(3) != "C")
            { throw new InvalidOperationException("Seed requires PostgreSQL 18.6, UTF8, UTC and C collation."); }
        }
        await using (var inventory = new NpgsqlCommand("SELECT schemaname||'.'||tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY 1", connection))
        {
            var allowed = Tables().Select(t => t.Name).Append("public.__EFMigrationsHistory").ToHashSet(StringComparer.Ordinal);
            await using var reader = await inventory.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            { if (!allowed.Contains(reader.GetString(0))) { throw new InvalidOperationException("An unknown table prevents seeding this database."); } }
        }
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(20261024)", connection, transaction)) { await guard.ExecuteNonQueryAsync(ct); }
        var manifestPath = Path.Combine(output, "seed-manifest.json");
        var populated = false;
        foreach (var table in Tables())
        {
            await using var exists = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {table.Name})", connection, transaction);
            populated |= (bool)(await exists.ExecuteScalarAsync(ct))!;
        }
        if (populated && !File.Exists(manifestPath)) { throw new InvalidOperationException("A populated database requires its previous verified manifest; nothing was removed."); }
        if (!populated)
        {
            foreach (var table in Tables().Where(t => t.Types.Length > 0))
            {
                Console.WriteLine("Seeding " + table.Name);
                await using var writer = await connection.BeginBinaryImportAsync($"COPY {table.Name}({table.Columns}) FROM STDIN (FORMAT BINARY)", ct);
                foreach (var row in table.Rows(false))
                {
                    ct.ThrowIfCancellationRequested(); await writer.StartRowAsync(ct);
                    for (var i = 0; i < row.Length; i++)
                    { if (row[i] is null) { await writer.WriteNullAsync(ct); } else { await writer.WriteAsync(row[i]!, table.Types[i], ct); } }
                }
                await writer.CompleteAsync(ct);
            }
        }
        Console.WriteLine("Verifying canonical rows against independent regeneration");
        var actual = await ActualChecksumsAsync(connection, ct); var expected = ExpectedChecksums(ct);
        if (!actual.OrderBy(p => p.Key).SequenceEqual(expected.OrderBy(p => p.Key))) { throw new InvalidOperationException("Canonical seed checksums differ from independent generation."); }
        var manifest = new BenchmarkManifest(Profile, Seed, "1", BenchmarkManifest.Hash(Corpus), BenchmarkManifest.Schema(db), issuer, actual,
            await BenchmarkManifest.EnvironmentAsync(connection, ct, postgresImage), VerifyDistribution());
        if (populated)
        {
            var old = JsonSerializer.Deserialize<BenchmarkManifest>(await File.ReadAllTextAsync(manifestPath, ct), BenchmarkManifest.Json)!;
            if (old.Profile != Profile || old.Seed != Seed || old.Generator != manifest.Generator || old.Issuer != issuer || old.CorpusSha256 != manifest.CorpusSha256 || old.SchemaSha256 != manifest.SchemaSha256 || !old.Tables.OrderBy(p => p.Key).SequenceEqual(actual.OrderBy(p => p.Key)))
            { throw new InvalidOperationException("The stored manifest does not match this database and generator."); }
        }
        await transaction.CommitAsync(ct);
        await using (var analyze = new NpgsqlCommand("ANALYZE", connection) { CommandTimeout = 300 }) { await analyze.ExecuteNonQueryAsync(ct); }
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, BenchmarkManifest.Json), ct);
        return manifest;
    }
    public JsonElement VerifyDistribution()
    {
        var categories = new int[200]; var tags = new int[2000]; var sizes = new Dictionary<int, int>(); var authors = new int[1000];
        long likes = 0, comments = 0, views = 0;
        for (var n = 0; n < PublicationCount; n++)
        {
            foreach (var c in Categories(n)) { categories[c]++; }
            foreach (var t in Tags(n)) { tags[t]++; }
            if (Categories(n).Distinct().Count() != 2 || Tags(n).Distinct().Count() != 5) { throw new InvalidOperationException("Duplicate assignments."); }
            var bytes = BodyBytes(n); sizes[bytes] = sizes.GetValueOrDefault(bytes) + 1; authors[Rank("authors", n) % 1000]++;
            likes += LikeCount(n); comments += RelationCount(Rank("comments", n)); views += 10 + 9 * LikeCount(n);
        }
        if (likes != 1_000_000 || comments != 1_000_000 || views != 10_000_000 || authors.Any(c => c != 100)
            || categories.Take(10).Any(c => c != 10000) || categories.Skip(10).Take(40).Any(c => c != 2000) || categories.Skip(50).Any(c => c is not (133 or 134))
            || tags.Take(20).Any(c => c != 10000) || tags.Skip(20).Take(180).Any(c => c != 1000) || tags.Skip(200).Any(c => c is not (66 or 67)))
        { throw new InvalidOperationException("The generated distribution violates sc005-v1."); }
        return JsonSerializer.SerializeToElement(new { publications = PublicationCount, users = 10_000, authors, categories, tags, sizes, likes, comments, views, commentStates = new[] { 900_000, 50_000, 50_000 } });
    }
}
