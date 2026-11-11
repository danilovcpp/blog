using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Media;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlogEngine.IntegrationTests.Operations;

public sealed class MediaRecoveryTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    private const string LegacyCover = "https://fixture.invalid/legacy-cover.png";
    private const string PreviousMigration = "20261024081130_AddViewDeduplication";
    private sealed record ObjectManifest(MediaBucketKind Bucket, string Key, long Length, byte[] Hash);
    private sealed record Expected(string Issuer, Guid Subject, Guid Publication, Guid Image, Guid Attachment, string Quota, string Avatar,
        Dictionary<string, string> Roles, ObjectManifest[] Objects);
    private sealed record RecoveryManifest(Guid RecoverySetId, string Postgres, string Garage, string Schema, Dictionary<string, string> Checksums);

    [Fact]
    public async Task EncryptedRecoverySetRestoresWholeVolumesSqlGrantsLinksAvatarsQuotaAndRollback()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Environment.GetEnvironmentVariable("BLOG_MEDIA_RECOVERY_DIRECTORY") ??
            PublishingFixture.ArtifactPath("media/phase6/recovery-set-" + Guid.NewGuid().ToString("N"));
        var input = Environment.GetEnvironmentVariable("BLOG_MEDIA_RECOVERY_INPUT") == "1";
        var suppliedKey = Environment.GetEnvironmentVariable("BLOG_MEDIA_RECOVERY_KEY_BASE64");
        if (input && suppliedKey is null) { throw new InvalidOperationException("A protected recovery key is required for an existing set."); }
        var key = suppliedKey is null ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(suppliedKey);
        Assert.Equal(32, key.Length); Directory.CreateDirectory(directory);
        try
        {
            if (!input) { await CreateAsync(directory, key, ct); }
            var manifest = JsonSerializer.Deserialize<RecoveryManifest>(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), ct))!;
            Assert.Equal(PostgresImage.Reference, manifest.Postgres); Assert.Equal(GarageImage.Reference, manifest.Garage);
            async Task<byte[]> ReadAsync(string name)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, name), ct);
                Assert.Equal(manifest.Checksums[name], Convert.ToHexString(SHA256.HashData(bytes))); return Decrypt(bytes, key, Context(manifest, name));
            }
            var expected = JsonSerializer.Deserialize<Expected>(await ReadAsync("expected.bin"))!;
            var keys = JsonSerializer.Deserialize<string[]>(await ReadAsync("keys.bin"))!;
            await using var identity = new JwksFixture(expected.Issuer); await identity.InitializeAsync();
            for (var pass = 0; pass < 2; pass++)
            {
                await using var restoredGarage = new GarageFixture();
                await restoredGarage.InitializeAsync(new(await ReadAsync("metadata.bin"), await ReadAsync("data.bin"), keys[0], keys[1]));
                await using var restoredDb = await postgres.CreateDatabaseAsync(s => new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(s).Options), ct);
                await EnsureRolesAsync(restoredDb, expected.Roles.Values, ct);
                await postgres.RestoreAsync(restoredDb, await ReadAsync("database.bin"), ct);
                await using var verify = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(restoredDb.ConnectionString).Options);
                Assert.Equal(manifest.Schema, (await verify.Database.GetAppliedMigrationsAsync(ct)).Last());
                Assert.Equal(LegacyCover, await verify.Publications.Where(p => p.Slug == "legacy-recovery").Select(p => p.CoverUrl).SingleAsync(ct));
                var generations = await verify.MediaUploads.AsNoTracking().OrderBy(u => u.Id).Select(u => u.LeaseGeneration).ToArrayAsync(ct);
                await verify.Database.ExecuteSqlRawAsync("UPDATE media.uploads SET lease_until=NULL; UPDATE media.objects SET lease_until=NULL; UPDATE media.cleanup_jobs SET lease_until=NULL", ct);
                Assert.Equal(generations, await verify.MediaUploads.AsNoTracking().OrderBy(u => u.Id).Select(u => u.LeaseGeneration).ToArrayAsync(ct));
                // Every API statement runs under the restored runtime principal, with a new private spool.
                var runtime = new NpgsqlConnectionStringBuilder(restoredDb.ConnectionString) { Options = "-c role=" + expected.Roles["runtime_role"] }.ConnectionString;
                await using var api = new ApiFactory(runtime, identity) { MediaStorage = restoredGarage };
                using var client = api.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken(expected.Subject));
                using var ready = await client.GetAsync("/health/ready", ct); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
                Assert.Equal(expected.Quota, (await PublishingFixture.JsonAsync(await client.GetAsync("/api/v1/media/allowance", ct), HttpStatusCode.OK, ct)).GetRawText());
                Assert.Equal(expected.Avatar, (await AvatarContractTests.StateAsync(client, ct)).GetRawText());
                var publication = await PublishingFixture.JsonAsync(await client.GetAsync($"/api/v1/workspace/publications/{expected.Publication}", ct), HttpStatusCode.OK, ct);
                Assert.Equal(expected.Image, publication.GetProperty("coverFileId").GetGuid()); Assert.Equal(expected.Attachment, publication.GetProperty("attachmentIds")[0].GetGuid());
                using var s3 = restoredGarage.CreateClient();
                foreach (var obj in expected.Objects)
                {
                    using var response = await s3.GetObjectAsync(obj.Bucket == MediaBucketKind.Originals ? restoredGarage.OriginalsBucket : restoredGarage.RenditionsBucket, obj.Key, ct);
                    Assert.Equal(obj.Length, response.ContentLength); Assert.Equal(obj.Hash, await SHA256.HashDataAsync(response.ResponseStream, ct));
                }
                await restoredGarage.AssertRuntimePermissionsAsync(ct);
                using var stranger = api.CreateClient(); stranger.DefaultRequestHeaders.Authorization = new("Bearer", identity.CreateToken());
                using var hidden = await stranger.GetAsync($"/api/v1/media/files/{expected.Attachment}", ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
                using var guarded = await client.DeleteAsync($"/api/v1/media/files/{expected.Image}", ct); Assert.Equal(HttpStatusCode.Conflict, guarded.StatusCode);
                // This post-point reservation must be absent when the saved set is restored again.
                await UploadContractTests.InitAsync(client, 13, ct);
            }
            var report = Environment.GetEnvironmentVariable("BLOG_MEDIA_VALIDATION_DIRECTORY") is null ? "media/recovery.md" : "media/phase6/recovery.md";
            await File.WriteAllTextAsync(PublishingFixture.ArtifactPath(report), $"# Recovery rehearsal\n\nRecovery set: {manifest.RecoverySetId:D}\n\nPASS: SQL upgraded from {PreviousMigration} with a legacy external cover. The encrypted set of stopped Garage volumes and pg_dump restored twice into fresh volumes/database. The legacy cover, exact pins, schema, hashes, links, avatars, quota, runtime permissions and lease generations survived. Post-point writes are absent after rollback. Garage remained on its pinned release throughout the rehearsal.\n", ct);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task CreateAsync(string directory, byte[] key, CancellationToken ct)
    {
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage, upgradeFrom: PreviousMigration, seedBeforeUpgrade: SeedLegacyAsync);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var image = await PublicationMediaReadTests.ImageAsync(fixture, author, PublicationMediaReadTests.Image(), ct);
        var attachment = await PublicationMediaWriteTests.AttachmentAsync(fixture, author, ct);
        await AvatarContractTests.AssignAsync(fixture, author, ct);
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        { title = "recovery", slug = "recovery", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverFileId = image, attachmentIds = new[] { attachment } }, ct), HttpStatusCode.Created, ct);
        await using var db = MediaCleanupTests.Context(fixture);
        var objects = await db.MediaObjects.AsNoTracking().Where(o => o.State == MediaObjectState.Present)
            .Select(o => new ObjectManifest(o.BucketKind, o.ObjectKey, o.VerifiedLength!.Value, o.Sha256!)).ToArrayAsync(ct);
        var user = await db.LocalUsers.SingleAsync(u => u.Issuer == fixture.Identity.Issuer, ct);
        var expected = new Expected(fixture.Identity.Issuer, user.Subject, publication.GetProperty("id").GetGuid(), image, attachment,
            (await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/allowance", ct), HttpStatusCode.OK, ct)).GetRawText(),
            (await AvatarContractTests.StateAsync(author, ct)).GetRawText(), fixture.Roles, objects);
        await fixture.Factory.DisposeAsync();
        var dump = await postgres.DumpAsync(fixture.Database, ct); var snapshot = await garage.SnapshotAsync(ct);
        var payloads = new Dictionary<string, byte[]>
        {
            ["database.bin"] = dump,
            ["metadata.bin"] = snapshot.Metadata,
            ["data.bin"] = snapshot.Data,
            ["keys.bin"] = JsonSerializer.SerializeToUtf8Bytes(new[] { snapshot.AccessKey, snapshot.SecretKey }),
            ["expected.bin"] = JsonSerializer.SerializeToUtf8Bytes(expected),
        };
        var checksums = new Dictionary<string, string>();
        var manifest = new RecoveryManifest(Guid.NewGuid(), PostgresImage.Reference, GarageImage.Reference, (await db.Database.GetAppliedMigrationsAsync(ct)).Last(), checksums);
        foreach (var (name, bytes) in payloads)
        {
            var encrypted = Encrypt(bytes, key, Context(manifest, name)); await File.WriteAllBytesAsync(Path.Combine(directory, name), encrypted, ct);
            checksums[name] = Convert.ToHexString(SHA256.HashData(encrypted));
        }
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest), ct);
    }

    private static async Task SeedLegacyAsync(PostgresDatabase database, CancellationToken ct)
    {
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(database.ConnectionString).Options);
        var owner = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts.local_users(id,issuer,subject,created_at) VALUES ({owner},'urn:fixture:legacy',{Guid.NewGuid()},clock_timestamp());
            INSERT INTO publishing.publications(id,author_id,title,slug,markdown,cover_url,state,version,created_at,updated_at)
            VALUES ({Guid.NewGuid()},{owner},'Legacy','legacy-recovery','legacy text',{LegacyCover},0,{Guid.NewGuid()},clock_timestamp(),clock_timestamp());
            """, ct);
    }

    private static async Task EnsureRolesAsync(PostgresDatabase database, IEnumerable<string> roles, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync(ct);
        foreach (var role in roles)
        {
            if (!role.StartsWith("pub_", StringComparison.Ordinal) || role.Length != 36 || role.Skip(4).Any(c => !char.IsAsciiHexDigit(c)))
            { throw new InvalidOperationException("Recovery rehearsal accepts only fixture-owned role names."); }
            await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@role)", connection); exists.Parameters.AddWithValue("role", role);
            if ((bool)(await exists.ExecuteScalarAsync(ct))!) { continue; }
            await using var create = new NpgsqlCommand("CREATE ROLE \"" + role + "\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE", connection); await create.ExecuteNonQueryAsync(ct);
        }
    }
    private static byte[] Context(RecoveryManifest manifest, string part) => Encoding.UTF8.GetBytes(
        $"{manifest.RecoverySetId:D}/{manifest.Postgres}/{manifest.Garage}/{manifest.Schema}/{part}");

    private static byte[] Encrypt(byte[] plaintext, byte[] key, byte[] context)
    {
        var result = new byte[28 + plaintext.Length]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16); aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16), context); return result;
    }
    private static byte[] Decrypt(byte[] encrypted, byte[] key, byte[] context)
    {
        var plaintext = new byte[encrypted.Length - 28]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plaintext, context); return plaintext;
    }
}
