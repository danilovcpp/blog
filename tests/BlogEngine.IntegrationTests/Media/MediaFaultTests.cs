using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Maintenance;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Media;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlogEngine.IntegrationTests.Media;

public sealed class MediaFaultTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    [Fact]
    public async Task TruncatedStorageGetAndInsufficientStoragePutAreRetryableWithoutFalseSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var file = await UploadContractTests.InitAsync(author, 64, ct);
        var original = new byte[64];
        using (var put = await UploadContractTests.PutAsync(author, file.GetProperty("id").GetGuid(), original, ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        await UploadContractTests.FinishAsync(fixture, author, file.GetProperty("id").GetGuid(), ct);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var fault = builder.Build();
        fault.MapGet("/{**path}", async context =>
        {
            context.Response.ContentLength = original.Length;
            await context.Response.Body.WriteAsync(new byte[1], context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            context.Abort();
        });
        fault.MapPut("/{**path}", () => Results.Content("<Error><Code>InsufficientStorage</Code></Error>", "application/xml", statusCode: 507));
        await fault.StartAsync(ct);
        var endpoint = fault.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var failing = fixture.Factory.WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Media:Storage:Endpoint"] = endpoint })));
        using var client = failing.CreateClient(); client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var path = $"/api/v1/media/files/{file.GetProperty("fileId").GetGuid()}/content";
        using var failedGet = await client.GetAsync(path, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedGet.StatusCode);
        Assert.Null(failedGet.Content.Headers.ContentDisposition);
        var pending = await UploadContractTests.InitAsync(author, 1, ct); var id = pending.GetProperty("id").GetGuid();
        using var failedPut = await UploadContractTests.PutAsync(client, id, [9], ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedPut.StatusCode);
        var state = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("AwaitingContent", state.GetProperty("state").GetString());
        using var recovered = await author.GetAsync(path, ct);
        Assert.Equal(original, await recovered.Content.ReadAsByteArrayAsync(ct));
        using var retry = await UploadContractTests.PutAsync(author, id, [9], ct);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    [Fact]
    public async Task LostResponseAfterSqlCommitPreservesAcceptedBytesAndDoesNotSpendQuotaTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var upload = await UploadContractTests.InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var failing = new ApiFactory(connection, fixture.Identity)
        {
            MediaStorage = garage,
            CustomizeServices = services => services.AddDbContext<BlogDbContext>(options => options.AddInterceptors(new LoseAcceptedCommit(id))),
        };
        using var client = failing.CreateClient(); client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        using var first = await UploadContractTests.PutAsync(client, id, [42], ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        var state = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("ContentReceived", state.GetProperty("state").GetString());
        using var retry = await UploadContractTests.PutAsync(author, id, [42], ct); Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        await UploadContractTests.FinishAsync(fixture, author, id, ct);
        await using var db = MediaCleanupTests.Context(fixture);
        Assert.Equal(1, await db.MediaObjects.CountAsync(ct));
        Assert.Equal(1, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes);
    }

    [Fact]
    public async Task ReplicaRestartAfterDurableDisplayPlanMakesOneReadyImageAndRetiresAbandonedAttempt()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var bytes = PublicationMediaReadTests.Image();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
        { Content = JsonContent.Create(new { purpose = "PublicationImage", name = "plan.png", declaredSize = bytes.Length, declaredContentType = "image/png" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var upload = await PublishingFixture.JsonAsync(await author.SendAsync(request, ct), HttpStatusCode.Created, ct);
        var id = upload.GetProperty("id").GetGuid();
        using (var put = await UploadContractTests.PutAsync(author, id, bytes, ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        using (var accepted = await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        Guid planned;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var claim = await scope.ServiceProvider.GetRequiredService<IMediaProcessingStore>().ClaimAsync(id, ct);
            Assert.NotNull(claim);
            planned = (await scope.ServiceProvider.GetRequiredService<IPublicationImageStore>().PlanDisplayAsync(claim, ct)).Id;
        }
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        var bearer = author.DefaultRequestHeaders.Authorization;
        await fixture.Factory.DisposeAsync();
        clock.Now = clock.Now.AddMinutes(5);
        await using var restarted = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage, ViewClock = clock };
        using var client = restarted.CreateClient(); client.DefaultRequestHeaders.Authorization = bearer;
        using (var scope = restarted.Services.CreateScope())
        { await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, ct); }
        var result = await PublishingFixture.JsonAsync(await client.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(upload.GetProperty("fileId").GetGuid(), result.GetProperty("fileId").GetGuid());
        await using var db = MediaCleanupTests.Context(fixture);
        Assert.Equal(MediaObjectState.Retired, (await db.MediaObjects.SingleAsync(o => o.Id == planned, ct)).State);
        Assert.Equal(1, await db.MediaRepresentations.CountAsync(ct));
        Assert.Equal(bytes.Length, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes);
    }

    [Fact]
    public async Task AlreadyStreamingGaragePutCompletesAfterLeaseExpiryAndSuccessfulDeleteWithoutRevivingFile()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        const int length = 1024 * 1024;
        var upload = await UploadContractTests.InitAsync(author, length, ct); var id = upload.GetProperty("id").GetGuid();
        await using var db = MediaCleanupTests.Context(fixture); var owner = (await db.MediaUploads.SingleAsync(ct)).OwnerId;
        using var scope = fixture.Factory.Services.CreateScope(); var transfers = scope.ServiceProvider.GetRequiredService<IUploadTransferStore>();
        var claim = await transfers.ClaimAsync(owner, id, ct);
        var spool = fixture.Factory.Services.GetRequiredService<IMediaSpool>();
        await using var bytes = await spool.CaptureAsync(new MemoryStream(new byte[length]), length, ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = fixture.Factory.Services.GetRequiredService<IMediaObjectStorage>();
        var pending = storage.PutAsync(MediaBucketKind.Originals, claim.ObjectKey!, new GatedFile(bytes, entered, release.Task), ct);
        try
        {
            // The final 64KiB are withheld only from asynchronous HTTP transmission, after signed headers
            // and 960KiB have crossed the socket. Hashing uses the complete seekable source synchronously.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            Assert.False(pending.IsCompleted);
            clock.Now = claim.LeaseUntil;
            using var cancel = await author.DeleteAsync($"/api/v1/media/uploads/{id}", ct); Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
            await fixture.Factory.Services.GetRequiredService<MediaCleanupCycle>().RunAsync(ct);
            Assert.NotNull((await db.MediaCleanupJobs.AsNoTracking().SingleAsync(ct)).CompletedAt);
        }
        finally { release.TrySetResult(); }
        await pending;
        await Assert.ThrowsAsync<DomainError>(() => transfers.AcceptAsync(claim, bytes, ct));
        clock.Now = clock.Now.AddMinutes(5);
        await fixture.Factory.Services.GetRequiredService<MediaCleanupCycle>().RunAsync(ct);
        using var s3 = garage.CreateClient();
        await Assert.ThrowsAnyAsync<Amazon.S3.AmazonS3Exception>(() => s3.GetObjectAsync(garage.OriginalsBucket, claim.ObjectKey, ct));
        Assert.Equal(MediaFileState.Deleted, (await db.MediaFiles.AsNoTracking().SingleAsync(ct)).State);
    }

    [Fact]
    public async Task LeaseExpiryAndCancelCannotPublishLateSourceAndReconciliationDeletesItsBytes()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var init = await UploadContractTests.InitAsync(author, 1, ct); var uploadId = init.GetProperty("id").GetGuid();
        await using var db = MediaCleanupTests.Context(fixture); var owner = (await db.MediaUploads.SingleAsync(ct)).OwnerId;
        using var scope = fixture.Factory.Services.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<IUploadTransferStore>();
        var claim = await store.ClaimAsync(owner, uploadId, ct);
        var spool = fixture.Factory.Services.GetRequiredService<IMediaSpool>();
        await using var bytes = await spool.CaptureAsync(new MemoryStream(new byte[] { 1 }), 1, ct);
        using (var cancelled = await author.DeleteAsync($"/api/v1/media/uploads/{uploadId}", ct)) { Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode); }
        clock.Now = claim.LeaseUntil;
        var cycle = fixture.Factory.Services.GetRequiredService<MediaCleanupCycle>(); await cycle.RunAsync(ct);
        // Simulates the final write of an accepted operation arriving after cancellation and deletion.
        var storage = fixture.Factory.Services.GetRequiredService<IMediaObjectStorage>();
        await storage.PutAsync(MediaBucketKind.Originals, claim.ObjectKey!, bytes, ct);
        await Assert.ThrowsAsync<DomainError>(() => store.AcceptAsync(claim, bytes, ct));
        clock.Now = clock.Now.AddHours(2); await cycle.RunAsync(ct);
        using var s3 = garage.CreateClient();
        await Assert.ThrowsAnyAsync<Amazon.S3.AmazonS3Exception>(() => s3.GetObjectAsync(garage.OriginalsBucket, claim.ObjectKey, ct));
        Assert.Equal(MediaUploadState.Cancelled, (await db.MediaUploads.AsNoTracking().SingleAsync(ct)).State);
        Assert.Equal(MediaFileState.Deleted, (await db.MediaFiles.AsNoTracking().SingleAsync(ct)).State);
    }

    [Fact]
    public async Task CleanupOutageRetriesAutomaticallyAndLogicalDeletionReleasesQuotaImmediately()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var outsider = await fixture.ActorAsync(LocalRole.Author, ct);
        var item = await MediaCleanupTests.ReceivedAsync(author, ct); await UploadContractTests.FinishAsync(fixture, author, item.Upload, ct);
        await garage.StopAsync(ct);
        try
        {
            using var deleted = await author.DeleteAsync($"/api/v1/media/files/{item.File}", ct); Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using var replay = await author.DeleteAsync($"/api/v1/media/files/{item.File}", ct); Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
            using var hidden = await outsider.DeleteAsync($"/api/v1/media/files/{item.File}", ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            var quota = await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/allowance", ct), HttpStatusCode.OK, ct);
            Assert.Equal(0, quota.GetProperty("used").GetInt64());
            await fixture.Factory.Services.GetRequiredService<MediaCleanupCycle>().RunAsync(ct);
            await using var db = MediaCleanupTests.Context(fixture); var job = await db.MediaCleanupJobs.SingleAsync(ct);
            Assert.Null(job.CompletedAt); Assert.True(job.NextAttemptAt > clock.Now); Assert.Equal("media_storage_unavailable", job.ErrorCode);
        }
        finally { await garage.RestartAsync(ct); }
        clock.Now = clock.Now.AddMinutes(15);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await fixture.Factory.DisposeAsync();
        await using var restarted = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage, ViewClock = clock };
        var resumed = await restarted.Services.GetRequiredService<MediaCleanupCycle>().RunAsync(ct);
        Assert.Equal(1, resumed.Jobs);
        await using var verify = MediaCleanupTests.Context(fixture); Assert.NotNull((await verify.MediaCleanupJobs.SingleAsync(ct)).CompletedAt);
    }

    private sealed class LoseAcceptedCommit(Guid uploadId) : DbTransactionInterceptor
    {
        private int lost;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<MediaUpload>().Any(e => e.Entity.Id == uploadId && e.Entity.State == MediaUploadState.ContentReceived) == true &&
                Interlocked.Exchange(ref lost, 1) == 0)
            { throw new IOException("Fixture lost the result of a successful commit."); }
            return Task.CompletedTask;
        }
    }

    private sealed class GatedFile(IMediaSpoolFile inner, TaskCompletionSource entered, Task release) : IMediaSpoolFile
    {
        public long Length => inner.Length;
        public ReadOnlyMemory<byte> Sha256 => inner.Sha256;
        public ReadOnlyMemory<byte> Md5 => inner.Md5;
        public Stream OpenRead() => new GatedStream(inner.OpenRead(), entered, release);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class GatedStream(Stream inner, TaskCompletionSource entered, Task release) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var boundary = Length - 64 * 1024;
            if (Position >= boundary && !release.IsCompleted)
            { entered.TrySetResult(); await release.WaitAsync(ct); }
            var count = Position < boundary ? (int)Math.Min(buffer.Length, boundary - Position) : buffer.Length;
            return await inner.ReadAsync(buffer[..count], ct);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } base.Dispose(disposing); }
    }
}
