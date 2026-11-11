using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlogEngine.IntegrationTests.Media;

public sealed class UploadContractTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    private static BlogDbContext Context(PublishingFixture fixture) => new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
    internal static async Task<JsonElement> InitAsync(HttpClient client, long size, CancellationToken ct, Guid? key = null, string name = "same.bin")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
        { Content = JsonContent.Create(new { purpose = "Attachment", name, declaredSize = size, declaredContentType = "application/octet-stream" }) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString("D"));
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.EndsWith(result.GetProperty("id").GetString()!, response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.True(result.TryGetProperty("result", out _)); Assert.True(result.TryGetProperty("errorCode", out _));
        return result;
    }

    internal static async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, byte[] bytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/uploads/{id}/content") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        return await client.SendAsync(request, ct);
    }

    internal static async Task<JsonElement> FinishAsync(PublishingFixture fixture, HttpClient client, Guid id, CancellationToken ct)
    {
        using var response = await client.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        // Drive the same persistent worker operation explicitly; no polling or timing-dependent assertion.
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BlogEngine.Core.Media.Application.UploadCompletion>().ProcessAsync(id, ct);
        return await PublishingFixture.JsonAsync(await client.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct), HttpStatusCode.OK, ct);
    }

    [Fact]
    public async Task UploadReplaysAndHistoricalCompletionSurviveRoleRevocationAndExpiry()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var key = Guid.NewGuid(); var upload = await InitAsync(author, 3, ct, key, " файл.bin ");
        var id = upload.GetProperty("id").GetGuid();
        Assert.Equal(upload.GetProperty("fileId").GetGuid(), (await InitAsync(author, 3, ct, key, "файл.bin")).GetProperty("fileId").GetGuid());
        using (var put = await PutAsync(author, id, [0, 255, 1], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        using (var replay = await PutAsync(author, id, [0, 255, 1], ct)) { Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode); }
        using (var conflict = await PutAsync(author, id, [0], ct)) { Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); }
        var result = await FinishAsync(fixture, author, id, ct);
        var owner = (await PublishingFixture.JsonAsync(await author.GetAsync("/api/v1/me", ct), HttpStatusCode.OK, ct)).GetProperty("id").GetGuid();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct);
        clock.Now = clock.Now.AddDays(2);
        Assert.Equal(id, (await InitAsync(author, 3, ct, key, "файл.bin")).GetProperty("id").GetGuid());
        var replayResult = await PublishingFixture.JsonAsync(await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(result.ToString(), replayResult.ToString());
        Assert.Equal(3, (await db.StorageAllowances.SingleAsync(a => a.OwnerId == owner, ct)).UsedBytes);
        var fileId = result.GetProperty("fileId").GetGuid();
        await using (var deleted = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE media.files SET state=2,original_object_id=NULL,deleted_at=clock_timestamp(),version={Guid.NewGuid()} WHERE id={fileId}", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE media.allowances SET used_bytes=0 WHERE owner_id={owner}", ct);
            await deleted.CommitAsync(ct);
        }
        var historical = await PublishingFixture.JsonAsync(await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(result.ToString(), historical.ToString());
        Assert.Equal(MediaFileState.Deleted, (await db.MediaFiles.AsNoTracking().SingleAsync(f => f.Id == fileId, ct)).State);
        Assert.Equal(0, (await db.StorageAllowances.AsNoTracking().SingleAsync(a => a.OwnerId == owner, ct)).UsedBytes);
    }

    [Fact]
    public async Task OwnershipRolesMismatchCancelAndEffectiveExpiry()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        var upload = await InitAsync(author, 3, ct); var id = upload.GetProperty("id").GetGuid();
        using (var hidden = await admin.GetAsync($"/api/v1/media/uploads/{id}", ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        using (var hidden = await PutAsync(admin, id, [1, 2, 3], ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        using (var mismatch = await PutAsync(author, id, [1, 2], ct)) { Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode); }
        var status = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("Rejected", status.GetProperty("state").GetString()); Assert.Equal("validation_failed", status.GetProperty("errorCode").GetString());
        using (var cancel = await author.DeleteAsync($"/api/v1/media/uploads/{id}", ct)) { Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode); }
        var expires = await InitAsync(author, 3, ct); clock.Now = clock.Now.AddHours(24);
        status = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{expires.GetProperty("id").GetGuid()}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("Expired", status.GetProperty("state").GetString());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads") { Content = JsonContent.Create(new { purpose = "Attachment", name = "x", declaredSize = 1, declaredContentType = "text/plain" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var denied = await reader.SendAsync(request, ct); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData(LocalRole.Reader, 403)]
    [InlineData(LocalRole.Moderator, 403)]
    [InlineData(LocalRole.Author, 201)]
    [InlineData(LocalRole.Admin, 201)]
    public async Task NewInitUsesVerifiedLocalRolesAndStrictRequestContract(LocalRole? role, int expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var client = role.HasValue ? await fixture.ActorAsync(role.Value, ct) : fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
        { Content = JsonContent.Create(new { purpose = "Attachment", name = "x", declaredSize = 1, declaredContentType = "text/plain" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request, ct); Assert.Equal(expected, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        if (expected != 201) { Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType); }
    }

    [Fact]
    public async Task IdempotencyStrictBindingAcceptAndUnknownResponsesDoNotDuplicateReservations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var key = Guid.NewGuid(); var upload = await InitAsync(author, 1, ct, key); var id = upload.GetProperty("id").GetGuid();
        using (var conflict = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
        { Content = JsonContent.Create(new { purpose = "Attachment", name = "changed", declaredSize = 1, declaredContentType = "text/plain" }) })
        {
            conflict.Headers.Add("Idempotency-Key", key.ToString()); using var response = await author.SendAsync(conflict, ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("idempotency_conflict", (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
        }
        using (var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
        { Content = JsonContent.Create(new { purpose = "Attachment", name = "x", declaredSize = 1, declaredContentType = "text/plain", ownerId = Guid.NewGuid() }) })
        {
            unknown.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); using var response = await author.SendAsync(unknown, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var purpose in new object[] { 0, "0", "attachment", "Unknown" })
        {
            using var invalidPurpose = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/uploads")
            { Content = JsonContent.Create(new { purpose, name = "x", declaredSize = 1, declaredContentType = "text/plain" }) };
            invalidPurpose.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await author.SendAsync(invalidPurpose, ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var badAccept = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/uploads/{id}/content") { Content = new ByteArrayContent([1]) })
        {
            badAccept.Headers.Accept.Add(new("text/html")); badAccept.Content.Headers.ContentType = new("application/octet-stream");
            using var response = await author.SendAsync(badAccept, ct); Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        }
        using (var wrongType = await author.PutAsync($"/api/v1/media/uploads/{id}/content", new StringContent("x"), ct))
        { Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode); }
        using (var put = await PutAsync(author, id, [99], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        // Discard a committed completion response and recover exclusively through the original identity/key.
        await FinishAsync(fixture, author, id, ct);
        var status = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("Completed", status.GetProperty("state").GetString());
        Assert.Equal(id, (await InitAsync(author, 1, ct, key)).GetProperty("id").GetGuid());
        await using var db = Context(fixture);
        Assert.Equal(1, await db.MediaUploads.CountAsync(ct)); Assert.Equal(1, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes);
        using var invalid = fixture.Factory.CreateClient(); invalid.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        using var denied = await invalid.GetAsync($"/api/v1/media/uploads/{id}", ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokeCommittedBeforeCompleteOrWorkerRejectsAndReleasesReservation(bool afterProcessing)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new ViewCommandGate(sql => sql.Contains("pg_advisory_xact_lock", StringComparison.Ordinal));
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: gate, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var upload = await InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
        using (var put = await PutAsync(author, id, [8], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        if (afterProcessing)
        { using var accepted = await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        await using var revoker = Context(fixture); var owner = (await revoker.MediaUploads.SingleAsync(ct)).OwnerId;
        await using var tx = await revoker.Database.BeginTransactionAsync(ct);
        await revoker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({RoleAdministrationStore.LockKey(owner)})", ct);
        await revoker.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct);
        gate.Arm();
        Task operation;
        HttpResponseMessage? response = null;
        using var scope = fixture.Factory.Services.CreateScope();
        if (afterProcessing) { operation = scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, ct); }
        else { operation = CompleteAsync(); }
        try { await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        finally { gate.Release.TrySetResult(); }
        await tx.CommitAsync(ct); await operation.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (response is not null) { using (response) { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); } }
        var status = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("Rejected", status.GetProperty("state").GetString()); Assert.Equal("forbidden", status.GetProperty("errorCode").GetString());
        await using var verify = Context(fixture);
        Assert.Equal(0, (await verify.StorageAllowances.SingleAsync(ct)).UsedBytes);
        Assert.False(await verify.MediaUploads.AnyAsync(u => u.State == MediaUploadState.AwaitingContent || u.State == MediaUploadState.ContentReceived || u.State == MediaUploadState.Processing, ct));
        Assert.Single(await verify.MediaCleanupJobs.ToArrayAsync(ct));
        async Task CompleteAsync() { response = await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct); }
    }

    [Fact]
    public async Task ReadyHoldingRoleLockCommitsBeforeWaitingRevocationAndKeepsHistoricalReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new ViewCommandGate(sql => sql.Contains("UPDATE media.files", StringComparison.Ordinal));
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: gate, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var upload = await InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
        using (var put = await PutAsync(author, id, [8], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        using (var accepted = await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        using var scope = fixture.Factory.Services.CreateScope();
        gate.Arm(); var ready = scope.ServiceProvider.GetRequiredService<UploadCompletion>().ProcessAsync(id, ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await using var revoker = Context(fixture); var owner = (await revoker.MediaUploads.SingleAsync(ct)).OwnerId;
        await using var tx = await revoker.Database.BeginTransactionAsync(ct);
        var revokeLock = revoker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({RoleAdministrationStore.LockKey(owner)})", ct);
        Assert.False(revokeLock.IsCompleted);
        gate.Release.TrySetResult(); await ready.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await revokeLock; await revoker.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct); await tx.CommitAsync(ct);
        var result = await PublishingFixture.JsonAsync(await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(id, result.GetProperty("uploadId").GetGuid());
        await using var verify = Context(fixture); Assert.Equal(1, (await verify.StorageAllowances.SingleAsync(ct)).UsedBytes);
    }

    [Fact]
    public async Task RevokedPutDoesNotReadOrMutateButAnEarlierClaimMayOnlyStageContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var upload = await InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
        await using var db = Context(fixture); var owner = (await db.MediaUploads.SingleAsync(ct)).OwnerId;
        using var scope = fixture.Factory.Services.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<IUploadTransferStore>();
        var claim = await store.ClaimAsync(owner, id, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct);
        var spool = scope.ServiceProvider.GetRequiredService<IMediaSpool>();
        await using var content = await spool.CaptureAsync(new MemoryStream([7]), 1, ct);
        await scope.ServiceProvider.GetRequiredService<IMediaObjectStorage>().PutAsync(MediaBucketKind.Originals, claim.ObjectKey!, content, ct);
        await store.AcceptAsync(claim, content, ct);
        using var denied = await PutAsync(author, id, [7], ct); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var readBody = false;
        await Assert.ThrowsAsync<BlogEngine.Core.Shared.DomainError>(() => scope.ServiceProvider.GetRequiredService<UploadContentService>().PutAsync(
            new CurrentActor(owner, [LocalRole.Author]), id, (_, _) => { readBody = true; return Task.FromResult(content); }, ct));
        Assert.False(readBody);
        var status = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("ContentReceived", status.GetProperty("state").GetString());
        using var complete = await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct); Assert.Equal(HttpStatusCode.Forbidden, complete.StatusCode);
        db.ChangeTracker.Clear(); Assert.Equal(0, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes);
        Assert.Equal(MediaFileState.Deleted, (await db.MediaFiles.SingleAsync(ct)).State);
    }

    [Fact]
    public async Task ConcurrentInitWithOneKeyReservesOnceAndAllUploadStatesStayOwnerOnly()
    {
        var ct = TestContext.Current.CancellationToken; var clock = new ControlledDatabaseClock();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, viewClock: clock, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var key = Guid.NewGuid(); var barrier = new ConcurrencyBarrier(20);
        var replays = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        { await barrier.SignalAndWaitAsync(ct); return await InitAsync(author, 1, ct, key); }));
        var first = replays[0]; var firstId = first.GetProperty("id").GetGuid();
        Assert.All(replays, r => Assert.Equal(firstId, r.GetProperty("id").GetGuid()));
        await using (var db = Context(fixture)) { Assert.Equal(1, await db.MediaUploads.CountAsync(ct)); }
        var states = new List<Guid> { firstId };
        foreach (var desired in new[] { "ContentReceived", "Processing", "Completed", "Rejected", "Cancelled", "Expired" })
        {
            var upload = await InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid(); states.Add(id);
            if (desired is "ContentReceived" or "Processing" or "Completed")
            {
                using var put = await PutAsync(author, id, [3], ct); Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
                if (desired == "Completed") { await FinishAsync(fixture, author, id, ct); }
                else if (desired == "Processing")
                { using var accepted = await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
            }
            else if (desired == "Rejected") { using var rejected = await PutAsync(author, id, [], ct); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); }
            else if (desired == "Cancelled") { using var cancelled = await author.DeleteAsync($"/api/v1/media/uploads/{id}", ct); Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode); }
            else { clock.Now = clock.Now.AddHours(24); }
            var status = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
            Assert.Equal(desired, status.GetProperty("state").GetString());
        }
        foreach (var role in new[] { LocalRole.Reader, LocalRole.Author, LocalRole.Moderator, LocalRole.Admin })
        {
            using var outsider = await fixture.ActorAsync(role, ct);
            foreach (var id in states)
            {
                foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
                {
                    using var request = new HttpRequestMessage(method, $"/api/v1/media/uploads/{id}");
                    using var hidden = await outsider.SendAsync(request, ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
                    Assert.DoesNotContain("same.bin", await hidden.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
                }
                using var complete = await outsider.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct);
                Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
            }
        }
        using var anonymous = await fixture.Anonymous.GetAsync($"/api/v1/media/uploads/{firstId}", ct); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task HostedWorkerResumesDurableProcessingAfterHostRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var upload = await InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
        using (var put = await PutAsync(author, id, [1], ct)) { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        using (var accepted = await author.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        var connection = fixture.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BlogEngine.Api.Configuration.ApiOptions>>().Value.ConnectionString;
        await fixture.Factory.DisposeAsync();
        var gate = new ViewCommandGate(sql => sql.Contains("UPDATE media.files", StringComparison.Ordinal)); gate.Arm();
        await using var restarted = new ApiFactory(connection, fixture.Identity, gate) { MediaStorage = garage, RunMediaWorker = true };
        using var client = restarted.CreateClient(); client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        try { await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        finally { gate.Release.TrySetResult(); }
        var result = await PublishingFixture.JsonAsync(await client.PostAsync($"/api/v1/media/uploads/{id}/complete", null, ct), HttpStatusCode.OK, ct);
        Assert.Equal(id, result.GetProperty("uploadId").GetGuid());
    }

    [Fact]
    public async Task AbortedBodyReleasesItsClaimAndDoesNotRejectOrConsumeQuota()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var upload = await InitAsync(author, 1, ct); var id = upload.GetProperty("id").GetGuid();
        await using var db = Context(fixture); var owner = (await db.MediaUploads.SingleAsync(ct)).OwnerId;
        using var scope = fixture.Factory.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<UploadContentService>();
        var error = await Assert.ThrowsAsync<BlogEngine.Core.Shared.DomainError>(() => service.PutAsync(new CurrentActor(owner, [LocalRole.Author]), id,
            (_, _) => Task.FromException<IMediaSpoolFile>(new IOException("interrupted body")), ct));
        Assert.Equal("media_storage_unavailable", error.Code);
        var state = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/media/uploads/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal("AwaitingContent", state.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, state.GetProperty("errorCode").ValueKind);
        using var retry = await PutAsync(author, id, [1], ct); Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(0, (await db.StorageAllowances.AsNoTracking().SingleAsync(ct)).UsedBytes);
    }
}
