using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlogEngine.IntegrationTests.Media;

public sealed class MediaAllowanceTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    [Fact]
    public async Task AllowanceUsesCurrentReservationsAtExactExpiryWithoutWorkerAndPreservesReadyFiles()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var ready = await UploadContractTests.InitAsync(author, 1, ct);
        var id = ready.GetProperty("id").GetGuid();
        using (var put = await UploadContractTests.PutAsync(author, id, [42], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        await UploadContractTests.FinishAsync(fixture, author, id, ct);
        await UploadContractTests.InitAsync(author, 7, ct);
        await CheckAsync(1, 7);
        clock.Now = clock.Now.AddHours(24).AddTicks(-10); await CheckAsync(1, 7);
        clock.Now = clock.Now.AddTicks(10); await CheckAsync(1, 0);
        clock.Now = clock.Now.AddDays(60); await CheckAsync(1, 0);
        using var download = await author.GetAsync($"/api/v1/media/files/{ready.GetProperty("fileId").GetGuid()}/content", ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        async Task CheckAsync(long used, long reserved)
        {
            var quota = await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/allowance", ct), HttpStatusCode.OK, ct);
            Assert.Equal(1_073_741_824, quota.GetProperty("limit").GetInt64()); Assert.Equal(used, quota.GetProperty("used").GetInt64());
            Assert.Equal(reserved, quota.GetProperty("reserved").GetInt64()); Assert.Equal(1_073_741_824 - used - reserved, quota.GetProperty("available").GetInt64());
        }
    }

    [Fact]
    public async Task HundredEqualReservationsLeaveExactlyTwentySuccessesAndAdminCanReadOtherOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        var first = await UploadContractTests.InitAsync(author, 1, ct);
        using (var cancel = await author.DeleteAsync($"/api/v1/media/uploads/{first.GetProperty("id").GetGuid()}", ct)) { Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode); }
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var owner = (await db.MediaUploads.SingleAsync(ct)).OwnerId;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE media.allowances SET limit_bytes=20 WHERE owner_id={owner}", ct);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 100).Select(async _ =>
        {
            await start.Task;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
            { Content = System.Net.Http.Json.JsonContent.Create(new { purpose = "Attachment", name = "safe.bin", declaredSize = 1, declaredContentType = "application/octet-stream" }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using var response = await author.SendAsync(request, ct);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return (response.StatusCode, json);
        }).ToArray();
        start.SetResult(); var results = await Task.WhenAll(requests);
        Assert.Equal(20, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(80, results.Count(r => r.StatusCode == HttpStatusCode.Conflict && r.json.GetProperty("code").GetString() == "quota_exceeded"));
        var quota = await PublishingFixture.JsonAsync(await admin.GetAsync($"/api/v1/media/allowance?ownerId={owner}", ct), HttpStatusCode.OK, ct);
        Assert.Equal(20, quota.GetProperty("reserved").GetInt64()); Assert.Equal(0, quota.GetProperty("available").GetInt64());
        using var denied = await author.GetAsync($"/api/v1/media/allowance?ownerId={owner}", ct); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var anonymous = await fixture.Anonymous.GetAsync("/api/v1/media/allowance", ct); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task HundredConcurrentFinalizationsAndDeletesAccountOnceAndHideTombstonesDuringStorageOutage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var other = await fixture.ActorAsync(LocalRole.Author, ct);
        var items = new List<(Guid Upload, Guid File)>();
        for (var i = 0; i < 100; i++) { items.Add(await MediaCleanupTests.ReceivedAsync(author, ct)); }
        foreach (var item in items)
        { using var accepted = await author.PostAsync($"/api/v1/media/uploads/{item.Upload}/complete", null, ct); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = items.Select(async item =>
        {
            await start.Task;
            using var scope = fixture.Factory.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(item.Upload, ct);
        }).ToArray();
        start.SetResult(); await Task.WhenAll(tasks);
        var before = await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/allowance", ct), HttpStatusCode.OK, ct);
        Assert.Equal(100, before.GetProperty("used").GetInt64()); Assert.Equal(0, before.GetProperty("reserved").GetInt64());
        await garage.StopAsync(ct);
        try
        {
            await Task.WhenAll(items.Select(async item =>
            {
                using var deleted = await author.DeleteAsync($"/api/v1/media/files/{item.File}", ct); Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                using var replay = await author.DeleteAsync($"/api/v1/media/files/{item.File}", ct); Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
                using var hidden = await other.DeleteAsync($"/api/v1/media/files/{item.File}", ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            }));
            var after = await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/media/allowance", ct), HttpStatusCode.OK, ct);
            Assert.Equal(0, after.GetProperty("used").GetInt64()); Assert.Equal(0, after.GetProperty("reserved").GetInt64());
        }
        finally { await garage.RestartAsync(ct); }
    }
}
