using System.Net;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BlogEngine.IntegrationTests.Media;

public sealed class UploadRetryTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownPutOutcomeOrProcessingFailureRemainsDurableAndRetriesExactlyOnce(bool processingFailure)
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var failing = new ApiFactory(connection, fixture.Identity)
        {
            MediaStorage = garage,
            ViewClock = clock,
            CustomizeServices = services =>
            {
                services.RemoveAll<IMediaObjectStorage>();
                services.AddSingleton<IMediaObjectStorage>(p => new LoseOneResponse(ActivatorUtilities.CreateInstance<GarageObjectStorage>(p), processingFailure));
            }
        };
        using var client = failing.CreateClient(); client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var upload = await UploadContractTests.InitAsync(client, 1, ct); var id = upload.GetProperty("id").GetGuid(); var fileId = upload.GetProperty("fileId").GetGuid();
        using (var first = await UploadContractTests.PutAsync(client, id, [17], ct))
        {
            Assert.Equal(processingFailure ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable, first.StatusCode);
            if (!processingFailure) { Assert.True(first.Headers.RetryAfter!.Delta > TimeSpan.Zero); }
        }
        if (!processingFailure)
        {
            var status = await PublishingFixture.JsonAsync(await client.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
            Assert.Equal("AwaitingContent", status.GetProperty("state").GetString());
            using var retry = await UploadContractTests.PutAsync(client, id, [17], ct); Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        }
        using (var accepted = await client.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        using (var scope = failing.Services.CreateScope()) { await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, ct); }
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        if (processingFailure)
        {
            var pending = await db.MediaUploads.AsNoTracking().SingleAsync(ct);
            Assert.Equal(MediaUploadState.Processing, pending.State); Assert.Null(pending.LeaseUntil); Assert.NotNull(pending.NextAttemptAt);
            Assert.Equal(0, (await db.StorageAllowances.AsNoTracking().SingleAsync(ct)).UsedBytes);
            clock.Now = pending.NextAttemptAt.Value;
            using var scope = failing.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, ct);
        }
        var result = await PublishingFixture.JsonAsync(await client.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(fileId, result.GetProperty("fileId").GetGuid());
        using var download = await client.GetAsync($"/api/v1/media/files/{fileId}/content", ct); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 17 }, await download.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(1, (await db.StorageAllowances.AsNoTracking().SingleAsync(ct)).UsedBytes);
        Assert.Equal(processingFailure ? 1 : 2, await db.MediaObjects.CountAsync(ct));
        Assert.Equal(processingFailure ? 0 : 1, await db.MediaCleanupJobs.CountAsync(ct));
    }

    private sealed class LoseOneResponse(IMediaObjectStorage inner, bool failGet) : IMediaObjectStorage, IDisposable
    {
        private int failed;
        public async Task PutAsync(MediaBucketKind bucket, string key, IMediaSpoolFile content, CancellationToken ct)
        {
            await inner.PutAsync(bucket, key, content, ct);
            if (!failGet && Interlocked.Exchange(ref failed, 1) == 0) { throw new MediaStorageException("fixture_lost_response", true); }
        }
        public async Task<IMediaSpoolFile> GetVerifiedAsync(MediaBucketKind bucket, string key, long length, ReadOnlyMemory<byte> hash, CancellationToken ct)
        {
            if (failGet && Interlocked.Exchange(ref failed, 1) == 0) { throw new MediaStorageException("fixture_storage_unavailable", true); }
            return await inner.GetVerifiedAsync(bucket, key, length, hash, ct);
        }
        public Task DeleteAsync(MediaBucketKind bucket, string key, CancellationToken ct) => inner.DeleteAsync(bucket, key, ct);
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }
}
