using System.Net;
using System.Text.Json;
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

public sealed class AvatarConcurrencyTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    [Fact]
    public async Task ConcurrentBootstrapConvergesAcrossMeAndAvatar()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<BlogEngine.Api.Configuration.ApiOptions>>().Value.ConnectionString;
        await using var second = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage };
        using var otherHost = second.CreateClient(); otherHost.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
        var barrier = new ConcurrencyBarrier(100);
        var versions = await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            await barrier.SignalAndWaitAsync(ct);
            var client = i % 3 == 0 ? reader : otherHost;
            var state = await PublishingFixture.JsonAsync(await client.GetAsync(i % 2 == 0 ? "/api/v1/me" : "/api/v1/me/avatar", ct), HttpStatusCode.OK, ct);
            return state.GetProperty(i % 2 == 0 ? "avatarVersion" : "version").GetGuid();
        }));
        Assert.Single(versions.Distinct()); Assert.NotEqual(Guid.Empty, versions[0]);
    }

    [Fact]
    public async Task HundredAcceptedCandidatesOfOneVersionApplyOnceAcrossHosts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        var version = (await AvatarContractTests.StateAsync(reader, ct)).GetProperty("version").GetGuid();
        var bytes = PublicationMediaReadTests.Image(); var candidates = new List<Guid>();
        // Prepare actual bytes before the race; transfer admission must not replace version conflicts.
        for (var i = 0; i < 100; i++)
        { candidates.Add((await AvatarContractTests.PrepareAsync(reader, version, bytes, ct)).GetProperty("id").GetGuid()); }
        var barrier = new ConcurrencyBarrier(100);
        await Task.WhenAll(candidates.Select(async id =>
        {
            await barrier.SignalAndWaitAsync(ct);
            using var accepted = await reader.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }));
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<BlogEngine.Api.Configuration.ApiOptions>>().Value.ConnectionString;
        await using var second = new ApiFactory(connection, fixture.Identity) { MediaStorage = garage };
        using var secondClient = second.CreateClient();
        // Let two real processing scopes contend for the owner/state lock, with independent spools.
        await Task.WhenAll(candidates.Chunk(50).Select(async (group, index) =>
        {
            foreach (var id in group)
            {
                using var scope = (index == 0 ? fixture.Factory : second).Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, ct);
            }
        }));
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var uploads = await db.MediaUploads.AsNoTracking().ToArrayAsync(ct);
        Assert.Single(uploads, u => u.State == MediaUploadState.Completed);
        Assert.Equal(99, uploads.Count(u => u.State == MediaUploadState.Rejected && u.ErrorCode == "concurrency_conflict"));
        Assert.Equal(bytes.Length, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes);
        Assert.Single(await db.MediaFiles.Where(f => f.State == MediaFileState.Ready).ToArrayAsync(ct));
        Assert.Equal(2, await db.MediaRepresentations.CountAsync(ct));
        var state = await AvatarContractTests.StateAsync(reader, ct);
        Assert.Equal(uploads.Single(u => u.State == MediaUploadState.Completed).FileId, state.GetProperty("fileId").GetGuid());
        var deleteBarrier = new ConcurrencyBarrier(100);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await deleteBarrier.SignalAndWaitAsync(ct);
            using var response = await PublishingFixture.SendAsync(reader, HttpMethod.Delete, "/api/v1/me/avatar", state.GetProperty("version").GetGuid(), null, ct);
            return response.StatusCode;
        }));
        Assert.Equal(1, results.Count(s => s == HttpStatusCode.OK)); Assert.Equal(99, results.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(0, (await db.StorageAllowances.AsNoTracking().SingleAsync(ct)).UsedBytes);
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("media/phase5/concurrency.json"), JsonSerializer.Serialize(new
        { Candidates = 100, Applied = 1, RejectedVersion = 99, DeleteSuccess = 1, DeleteConflicts = 99, Used = 0 }), ct);
    }

    [Fact]
    public async Task ReplacementReservesFullSizeAndHistoricalReplayNeverReassigns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        var original = await AvatarContractTests.AssignAsync(fixture, reader, ct);
        var state = await AvatarContractTests.StateAsync(reader, ct); var version = state.GetProperty("version").GetGuid();
        var bytes = PublicationMediaReadTests.Image(); var key = Guid.NewGuid();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var owner = (await db.AvatarStates.SingleAsync(ct)).UserId;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE media.allowances SET limit_bytes={bytes.Length * 2L - 1} WHERE owner_id={owner}", ct);
        using (var fullReserve = await AvatarContractTests.InitResponseAsync(reader, version, bytes, ct, key))
        { Assert.Equal(HttpStatusCode.Conflict, fullReserve.StatusCode); Assert.Contains("quota_exceeded", await fullReserve.Content.ReadAsStringAsync(ct), StringComparison.Ordinal); }
        Assert.Equal(state.GetRawText(), (await AvatarContractTests.StateAsync(reader, ct)).GetRawText());
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE media.allowances SET limit_bytes={bytes.Length * 2L} WHERE owner_id={owner}", ct);
        var upload = await PublishingFixture.JsonAsync(await AvatarContractTests.InitResponseAsync(reader, version, bytes, ct, key), HttpStatusCode.Created, ct);
        var id = upload.GetProperty("id").GetGuid();
        using (var put = await UploadContractTests.PutAsync(reader, id, bytes, ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        await UploadContractTests.FinishAsync(fixture, reader, id, ct);
        Assert.Equal(bytes.Length, (await db.StorageAllowances.AsNoTracking().SingleAsync(ct)).UsedBytes);
        var current = await AvatarContractTests.StateAsync(reader, ct);
        using (var changedVersion = await AvatarContractTests.InitResponseAsync(reader, current.GetProperty("version").GetGuid(), bytes, ct, key))
        { Assert.Equal(HttpStatusCode.Conflict, changedVersion.StatusCode); Assert.Contains("idempotency_conflict", await changedVersion.Content.ReadAsStringAsync(ct), StringComparison.Ordinal); }
        var replay = await PublishingFixture.JsonAsync(await AvatarContractTests.InitResponseAsync(reader, version, bytes, ct, key), HttpStatusCode.Created, ct);
        Assert.Equal(id, replay.GetProperty("id").GetGuid());
        using (var newStale = await AvatarContractTests.InitResponseAsync(reader, version, bytes, ct)) { Assert.Equal(HttpStatusCode.Conflict, newStale.StatusCode); }
        var historical = await PublishingFixture.JsonAsync(await reader.PostAsync($"/api/v1/media/uploads/{original.GetProperty("uploadId").GetGuid()}/complete", null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(original.GetRawText(), historical.GetRawText()); Assert.Equal(current.GetRawText(), (await AvatarContractTests.StateAsync(reader, ct)).GetRawText());
        var deleted = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(reader, HttpMethod.Delete, "/api/v1/me/avatar", current.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        replay = await PublishingFixture.JsonAsync(await AvatarContractTests.InitResponseAsync(reader, version, bytes, ct, key), HttpStatusCode.Created, ct);
        Assert.Equal(id, replay.GetProperty("id").GetGuid());
        Assert.Equal(deleted.GetRawText(), (await AvatarContractTests.StateAsync(reader, ct)).GetRawText());
    }

    [Fact]
    public async Task SwapDuringRenderingRejectsFinalizationAndRetiresAllNewObjects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        await AvatarContractTests.AssignAsync(fixture, reader, ct);
        var version = (await AvatarContractTests.StateAsync(reader, ct)).GetProperty("version").GetGuid();
        var bytes = PublicationMediaReadTests.Image(); var upload = await AvatarContractTests.PrepareAsync(reader, version, bytes, ct);
        var id = upload.GetProperty("id").GetGuid(); var fileId = upload.GetProperty("fileId").GetGuid();
        using (var accepted = await reader.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<BlogEngine.Api.Configuration.ApiOptions>>().Value.ConnectionString;
        await using var rendering = new ApiFactory(connection, fixture.Identity)
        {
            MediaStorage = garage,
            CustomizeServices = services =>
            {
                services.RemoveAll<IAvatarRenderer>();
                services.AddSingleton<IAvatarRenderer>(p => new GatedRenderer(ActivatorUtilities.CreateInstance<AvatarRenderer>(p), entered, release));
            }
        };
        using var client = rendering.CreateClient(); using var scope = rendering.Services.CreateScope();
        var processing = scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, ct);
        JsonElement winner;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            winner = await AvatarContractTests.AssignAsync(fixture, reader, ct);
        }
        finally { release.TrySetResult(); }
        await processing;
        var rejected = await PublishingFixture.JsonAsync(await reader.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("Rejected", rejected.GetProperty("state").GetString()); Assert.Equal("concurrency_conflict", rejected.GetProperty("errorCode").GetString());
        Assert.Equal(winner.GetProperty("fileId").GetGuid(), (await AvatarContractTests.StateAsync(reader, ct)).GetProperty("fileId").GetGuid());
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var objects = await db.MediaObjects.Where(o => o.FileId == fileId).ToArrayAsync(ct);
        Assert.Equal(3, objects.Length); Assert.All(objects, o => Assert.Equal(MediaObjectState.Retired, o.State));
        var objectIds = objects.Select(o => o.Id).ToArray();
        Assert.Equal(3, await db.MediaCleanupJobs.CountAsync(j => objectIds.Contains(j.ObjectId), ct));
        Assert.Equal(bytes.Length, (await db.StorageAllowances.AsNoTracking().SingleAsync(ct)).UsedBytes);
    }

    private sealed class GatedRenderer(IAvatarRenderer inner, TaskCompletionSource entered, TaskCompletionSource release) : IAvatarRenderer
    {
        public async Task<(NormalizedMediaImage Small, NormalizedMediaImage Large)> RenderAsync(IMediaSpoolFile source, string contentType, CancellationToken ct)
        {
            var images = await inner.RenderAsync(source, contentType, ct); entered.TrySetResult();
            try { await release.Task.WaitAsync(ct); return images; }
            catch { await images.Small.Content.DisposeAsync(); await images.Large.Content.DisposeAsync(); throw; }
        }
    }
}
