using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Maintenance;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Media;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.IntegrationTests.Media;

[Collection("Media transfers")]
public sealed class MediaCleanupTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    internal static BlogDbContext Context(PublishingFixture fixture) => new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
    internal static async Task<(Guid Upload, Guid File)> ReceivedAsync(HttpClient author, CancellationToken ct)
    {
        var upload = await UploadContractTests.InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
        using var put = await UploadContractTests.PutAsync(author, id, [73], ct); Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        return (id, upload.GetProperty("fileId").GetGuid());
    }

    [Fact]
    public async Task TwoReplicasFenceCleanupAndReconcileLatePutWithoutTouchingLibraryOrCurrentAvatar()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var kept = await ReceivedAsync(author, ct); await UploadContractTests.FinishAsync(fixture, author, kept.Upload, ct);
        var avatar = await AvatarContractTests.AssignAsync(fixture, author, ct);
        var abandoned = await ReceivedAsync(author, ct);
        using (var cancel = await author.DeleteAsync($"/api/v1/media/uploads/{abandoned.Upload}", ct)) { Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode); }
        await using var db = Context(fixture); var obj = await db.MediaObjects.AsNoTracking().SingleAsync(o => o.FileId == abandoned.File, ct);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var replica = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage, ViewClock = clock };
        using var first = fixture.Factory.Services.CreateScope(); using var second = replica.Services.CreateScope();
        var claims = await Task.WhenAll(first.ServiceProvider.GetRequiredService<MediaCleanupStore>().ClaimAsync(obj.Id, false, ct),
            second.ServiceProvider.GetRequiredService<MediaCleanupStore>().ClaimAsync(obj.Id, false, ct));
        var claim = Assert.Single(claims, c => c is not null)!;
        var storage = fixture.Factory.Services.GetRequiredService<IMediaObjectStorage>();
        await storage.DeleteAsync(claim.Bucket, claim.Key, ct);
        await first.ServiceProvider.GetRequiredService<MediaCleanupStore>().FinishAsync(claim, true, ct);
        // A Garage PUT already accepted before cancellation may finish even after DeleteObject.
        var spool = fixture.Factory.Services.GetRequiredService<IMediaSpool>();
        await using (var late = await spool.CaptureAsync(new MemoryStream(new byte[] { 73 }), 1, ct))
        { await storage.PutAsync(obj.BucketKind, obj.ObjectKey, late, ct); }
        clock.Now = clock.Now.AddMinutes(5);
        await replica.Services.GetRequiredService<MediaCleanupCycle>().RunAsync(ct);
        using var s3 = garage.CreateClient();
        await Assert.ThrowsAnyAsync<Amazon.S3.AmazonS3Exception>(() => s3.GetObjectAsync(garage.OriginalsBucket, obj.ObjectKey, ct));
        var deleted = await db.MediaObjects.AsNoTracking().SingleAsync(o => o.Id == obj.Id, ct);
        Assert.Equal(MediaObjectState.Deleted, deleted.State); Assert.NotNull(deleted.LastReconciledAt);
        using var content = await author.GetAsync($"/api/v1/media/files/{kept.File}/content", ct); Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        using var png = await fixture.Anonymous.GetAsync($"/api/v1/avatars/{avatar.GetProperty("fileId").GetGuid()}/256.png", ct); Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        using var staleComplete = await author.PostAsync($"/api/v1/media/uploads/{abandoned.Upload}/complete", null, ct); Assert.Equal(HttpStatusCode.Conflict, staleComplete.StatusCode);
    }

    [Fact]
    public async Task ExpiryUsesOriginalDeadlineAndInvalidatesProcessingFenceIncludingUploadsWithoutObjects()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var received = await ReceivedAsync(author, ct);
        using (var accepted = await author.PostAsync($"/api/v1/media/uploads/{received.Upload}/complete", null, ct)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        using var scope = fixture.Factory.Services.CreateScope();
        var processing = scope.ServiceProvider.GetRequiredService<IMediaProcessingStore>();
        var claim = await processing.ClaimAsync(received.Upload, ct); Assert.NotNull(claim);
        await UploadContractTests.InitAsync(author, 1, ct);
        clock.Now = clock.Now.AddDays(2);
        using var expiryScope = fixture.Factory.Services.CreateScope();
        Assert.Equal(2, await expiryScope.ServiceProvider.GetRequiredService<MediaExpiryStore>().RunBatchAsync(ct));
        await processing.CompleteAttachmentAsync(claim, ct);
        await using var db = Context(fixture);
        Assert.All(await db.MediaUploads.AsNoTracking().ToArrayAsync(ct), u => Assert.Equal(MediaUploadState.Expired, u.State));
        Assert.Equal(0, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes);
        var obj = await db.MediaObjects.SingleAsync(ct); Assert.Equal(clock.Now.AddDays(-1), obj.RetiredAt);
        Assert.Equal(0, await expiryScope.ServiceProvider.GetRequiredService<MediaExpiryStore>().RunBatchAsync(ct));
    }

    [Fact]
    public async Task FullBoundaryProfileScansTenThousandTombstonesAndExpiresBacklogWithinTwelveHoursWithArrivals()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var controls = new List<(Guid Id, byte[] Hash, string Name)>();
        for (var i = 0; i < 10; i++)
        {
            var bytes = PublicationMediaReadTests.Image(); var name = $"source-{i}.png";
            controls.Add((await PublicationMediaReadTests.ImageAsync(fixture, author, bytes, ct, name), SHA256.HashData(bytes), name));
        }
        await using var db = Context(fixture); var owner = (await db.MediaFiles.FirstAsync(ct)).OwnerId;
        var start = clock.Now;
        await SeedAsync(db, owner, 10_000, true, start.AddHours(-25), ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH prior AS (UPDATE media.objects SET state=3, last_reconciled_at={start.AddHours(-13)} WHERE id IN
                (SELECT id FROM media.objects WHERE state=2 ORDER BY id LIMIT 5000) RETURNING id)
            UPDATE media.cleanup_jobs SET completed_at={start.AddHours(-13)} WHERE object_id IN (SELECT id FROM prior)
            """, ct);
        await SeedAsync(db, owner, 1_000, false, start.AddHours(-25), ct);
        var settings = fixture.Factory.Services.GetRequiredService<IOptions<MediaOptions>>().Value.Cleanup;
        var interval = Environment.GetEnvironmentVariable("BLOG_MEDIA_CLEANUP_INTERVAL_SECONDS");
        var batch = Environment.GetEnvironmentVariable("BLOG_MEDIA_CLEANUP_BATCH_SIZE");
        if (interval is not null) { settings.Interval = TimeSpan.FromSeconds(int.Parse(interval, CultureInfo.InvariantCulture)); }
        if (batch is not null) { settings.BatchSize = int.Parse(batch, CultureInfo.InvariantCulture); }
        Assert.InRange(settings.Interval.TotalSeconds, 60, 900);
        Assert.InRange(settings.BatchSize, 1, 1_000);
        var ticks = (int)(TimeSpan.FromDays(1) / settings.Interval);
        var scanTicks = (int)(TimeSpan.FromHours(12) / settings.Interval);
        var cycle = fixture.Factory.Services.GetRequiredService<MediaCleanupCycle>();
        var totals = new List<object>();
        var measured = Stopwatch.StartNew();
        for (var tick = 0; tick < ticks; tick++)
        {
            clock.Now = start + settings.Interval * tick;
            var arrivals = (tick + 1) * 1_000 / ticks - tick * 1_000 / ticks;
            if (arrivals > 0)
            {
                await SeedAsync(db, owner, arrivals, true, clock.Now, ct);
                await SeedAsync(db, owner, arrivals, false, clock.Now.AddHours(-24), ct);
            }
            var timestamp = Stopwatch.GetTimestamp();
            var result = await cycle.RunAsync(ct);
            var elapsed = Stopwatch.GetElapsedTime(timestamp);
            Assert.True(elapsed < settings.Interval, $"Cleanup cycle took {elapsed} with interval {settings.Interval}.");
            Assert.InRange(result.Jobs, 0, settings.BatchSize); Assert.InRange(result.Expired, 0, settings.BatchSize); Assert.InRange(result.Reconciled, 0, settings.BatchSize);
            totals.Add(new { tick, result.Jobs, result.Expired, result.Reconciled, elapsedMs = elapsed.TotalMilliseconds });
            if (tick == scanTicks - 1)
            {
                Assert.Equal(0, await db.MediaObjects.CountAsync(o => o.RetiredAt < start && (o.LastReconciledAt == null || o.LastReconciledAt < start), ct));
                Assert.Equal(0, await db.MediaUploads.CountAsync(u => u.State == MediaUploadState.AwaitingContent && u.ExpiresAt <= start, ct));
            }
        }
        Assert.Equal(0, await db.MediaObjects.CountAsync(o => o.State == MediaObjectState.Retired && o.RetiredAt < start, ct));
        Assert.Equal(0, await db.MediaObjects.CountAsync(o => o.RetiredAt < start && o.LastReconciledAt == null, ct));
        Assert.Equal(0, await db.MediaUploads.CountAsync(u => u.State == MediaUploadState.AwaitingContent && u.ExpiresAt <= start, ct));
        foreach (var control in controls)
        {
            using var response = await author.GetAsync($"/api/v1/media/files/{control.Id}/content", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(control.Name, response.Content.Headers.ContentDisposition!.FileNameStar);
            Assert.Equal(control.Hash, SHA256.HashData(await response.Content.ReadAsByteArrayAsync(ct)));
        }
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("media/phase6/cleanup-profile.json"), JsonSerializer.Serialize(new
        {
            postgres = PostgresImage.Reference,
            garage = GarageImage.Reference,
            intervalSeconds = settings.Interval.TotalSeconds,
            settings.BatchSize,
            initialTombstones = 10_000,
            initialCompleted = 5_000,
            initialExpired = 1_000,
            newRetirements = 1_000,
            newExpiries = 1_000,
            scanHours = 12,
            drainHours = 24,
            wallSeconds = measured.Elapsed.TotalSeconds,
            cycles = totals,
            controls = controls.Select(c => new { c.Id, hash = Convert.ToHexString(c.Hash) }),
        }), ct);
    }

    [Fact]
    public async Task AllThreeQueueSelectionsAreBoundedAndExposeActualSqlPlans()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock(); var capture = new ReadingSqlCapture();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: capture, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); await UploadContractTests.InitAsync(author, 1, ct);
        await using var db = Context(fixture); var owner = (await db.MediaUploads.SingleAsync(ct)).OwnerId;
        await SeedAsync(db, owner, 101, true, clock.Now.AddDays(-2), ct);
        await SeedAsync(db, owner, 101, false, clock.Now.AddDays(-2), ct);
        capture.Clear();
        using var scope = fixture.Factory.Services.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<MediaCleanupStore>();
        Assert.Equal(100, (await store.DueAsync(false, ct)).Count); Assert.Equal(100, (await store.DueAsync(true, ct)).Count);
        Assert.Equal(100, await scope.ServiceProvider.GetRequiredService<MediaExpiryStore>().RunBatchAsync(ct));
        var candidates = capture.Commands.Where(c => c.Sql.Contains("ORDER BY", StringComparison.Ordinal) && c.Sql.Contains("LIMIT", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, candidates.Length);
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionString); await connection.OpenAsync(ct);
        var evidence = new List<object>();
        foreach (var candidate in candidates)
        {
            await using var explain = QueryInspectionTests.Command(connection, candidate, "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) ");
            var plan = JsonSerializer.Deserialize<JsonElement>((string)(await explain.ExecuteScalarAsync(ct))!);
            Assert.True(plan[0].TryGetProperty("Plan", out _));
            evidence.Add(new { sql = candidate.Sql, parameters = candidate.Parameters.Select(p => new { p.ParameterName, p.Value }), plan });
        }
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("media/phase6/queue-plans.json"), JsonSerializer.Serialize(evidence), ct);
    }

    private static async Task SeedAsync(BlogDbContext db, Guid owner, int count, bool retired, DateTimeOffset created, CancellationToken ct)
    {
        var files = Enumerable.Range(0, count).Select(_ => MediaFile.Create(owner, MediaPurpose.Attachment, "fixture.bin", "application/octet-stream", 1, created)).ToArray();
        var uploads = files.Select(f => MediaUpload.Create(f, Guid.NewGuid(), null, created)).ToArray();
        var objects = retired ? uploads.Select(u => MediaObject.Plan(u, MediaObjectKind.Source, created)).ToArray() : [];
        if (retired)
        {
            foreach (var file in files) { file.Delete(created); }
            foreach (var upload in uploads) { upload.Terminate(MediaUploadState.Cancelled, "cancelled", created); }
            foreach (var obj in objects) { obj.Retire(created); }
        }
        db.MediaFiles.AddRange(files); await db.SaveChangesAsync(ct);
        db.MediaUploads.AddRange(uploads); await db.SaveChangesAsync(ct);
        db.MediaObjects.AddRange(objects); await db.SaveChangesAsync(ct);
        db.MediaCleanupJobs.AddRange(objects.Select(o => MediaCleanupJob.Create(o.Id, created))); await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}
