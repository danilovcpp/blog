using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Accounts;
using BlogEngine.Infrastructure.Persistence.Publishing;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.IntegrationTests.Media;

public sealed class PublicationMediaWriteTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    private static BlogDbContext Context(PublishingFixture fixture) => new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
    internal static async Task<Guid> AttachmentAsync(PublishingFixture fixture, HttpClient author, CancellationToken ct)
    {
        var upload = await UploadContractTests.InitAsync(author, 3, ct);
        var id = upload.GetProperty("id").GetGuid();
        using var put = await UploadContractTests.PutAsync(author, id, [1, 2, 3], ct);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        return (await UploadContractTests.FinishAsync(fixture, author, id, ct)).GetProperty("fileId").GetGuid();
    }

    [Fact]
    public async Task OmissionPreservesLinksExplicitEmptyRemovesAndDeletionIsGuarded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var file = await AttachmentAsync(fixture, author, ct);
        var created = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        { title = "media", slug = "media", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { file } }, ct), HttpStatusCode.Created, ct);
        Assert.Equal(file, created.GetProperty("attachmentIds")[0].GetGuid());
        using (var blocked = await author.DeleteAsync($"/api/v1/media/files/{file}", ct))
        { Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode); }
        var path = $"/api/v1/publications/{created.GetProperty("id").GetGuid()}";
        var edited = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Put, path, created.GetProperty("version").GetGuid(), PublishingFixture.Content("media"), ct), HttpStatusCode.OK, ct);
        Assert.Equal(file, edited.GetProperty("attachmentIds")[0].GetGuid());
        var cleared = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Put, path, edited.GetProperty("version").GetGuid(), new
        { title = "clear", slug = "media", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = Array.Empty<Guid>() }, ct), HttpStatusCode.OK, ct);
        Assert.Empty(cleared.GetProperty("attachments").EnumerateArray());
        using (var deleted = await author.DeleteAsync($"/api/v1/media/files/{file}", ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        using (var replay = await author.DeleteAsync($"/api/v1/media/files/{file}", ct)) { Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode); }
    }

    [Fact]
    public async Task HundredWritesOfOneVersionHaveOneWinnerAndRollbackInvalidLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        var category = (await PublishingFixture.JsonAsync(await admin.PostAsJsonAsync("/api/v1/categories", new { name = "Media", slug = "media" }, ct), HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();
        var file = await AttachmentAsync(fixture, author, ct);
        var created = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("race", categories: [category]), ct), HttpStatusCode.Created, ct);
        var id = created.GetProperty("id").GetGuid(); var version = created.GetProperty("version").GetGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 100).Select(async i =>
        {
            await gate.Task;
            using var response = await PublishingFixture.SendAsync(author, HttpMethod.Put, $"/api/v1/publications/{id}", version, new
            { title = $"change {i}", slug = "race", markdown = "body", categoryIds = new[] { category }, tagIds = Array.Empty<Guid>(), attachmentIds = new[] { file } }, ct);
            return response.StatusCode;
        }).ToArray();
        gate.SetResult(); var statuses = await Task.WhenAll(tasks);
        Assert.Equal(1, statuses.Count(x => x == HttpStatusCode.OK)); Assert.Equal(99, statuses.Count(x => x == HttpStatusCode.Conflict));
        var current = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/workspace/publications/{id}", ct), HttpStatusCode.OK, ct);
        using var invalid = await PublishingFixture.SendAsync(author, HttpMethod.Put, $"/api/v1/publications/{id}", current.GetProperty("version").GetGuid(), new
        { title = "must rollback", slug = "race", markdown = "bad", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { Guid.NewGuid() } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var unchanged = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/workspace/publications/{id}", ct), HttpStatusCode.OK, ct);
        Assert.Equal(current.GetRawText(), unchanged.GetRawText());
    }

    [Fact]
    public async Task ManagedCoverEchoSwitchesAndInvalidLinksPreserveTheWholePublication()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var other = await fixture.ActorAsync(LocalRole.Author, ct);
        using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        var image = await PublicationMediaReadTests.ImageAsync(fixture, author, PublicationMediaReadTests.Image(), ct);
        var attachment = await AttachmentAsync(fixture, author, ct); var foreign = await AttachmentAsync(fixture, other, ct);
        var pending = (await UploadContractTests.InitAsync(author, 1, ct)).GetProperty("fileId").GetGuid();
        var created = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", new
        { title = "cover", slug = "cover", markdown = "original", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverFileId = image, inlineImageIds = new[] { image }, attachmentIds = new[] { attachment } }, ct), HttpStatusCode.Created, ct);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(image, created.GetProperty("coverFileId").GetGuid()); Assert.Equal(image, created.GetProperty("inlineImageIds")[0].GetGuid());
        var managedUrl = created.GetProperty("coverUrl").GetString();
        async Task<JsonElement> Edit(object body) => await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Put,
            $"/api/v1/publications/{id}", created.GetProperty("version").GetGuid(), body, ct), HttpStatusCode.OK, ct);
        created = await Edit(new { title = "echo", slug = "cover", markdown = "original", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverUrl = managedUrl });
        Assert.Equal(image, created.GetProperty("coverFileId").GetGuid());
        var invalidBodies = new object[]
        {
            new { title = "invalid", slug = "cover", markdown = "bad", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverFileId = image, coverUrl = "https://example.test/cover" },
            new { title = "invalid", slug = "cover", markdown = "bad", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverUrl = managedUrl!.Replace(id.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal) },
            new { title = "invalid", slug = "cover", markdown = "bad", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { foreign } },
            new { title = "invalid", slug = "cover", markdown = "bad", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { pending } },
            new { title = "invalid", slug = "cover", markdown = "bad", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { image } },
            new { title = "invalid", slug = "cover", markdown = "bad", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { attachment, attachment } },
            new { title = "invalid", slug = "cover", markdown = "bad", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), inlineImageIds = (Guid[]?)null },
        };
        foreach (var body in invalidBodies)
        {
            using var invalid = await PublishingFixture.SendAsync(admin, HttpMethod.Put, $"/api/v1/publications/{id}", created.GetProperty("version").GetGuid(), body, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var current = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/workspace/publications/{id}", ct), HttpStatusCode.OK, ct);
            Assert.Equal(created.GetRawText(), current.GetRawText());
        }
        created = await Edit(new { title = "external", slug = "cover", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverUrl = "https://example.test/cover" });
        Assert.Equal(JsonValueKind.Null, created.GetProperty("coverFileId").ValueKind); Assert.Single(created.GetProperty("inlineImages").EnumerateArray());
        created = await Edit(PublishingFixture.Content("cover"));
        Assert.Equal(JsonValueKind.Null, created.GetProperty("coverUrl").ValueKind);
        created = await Edit(new { title = "managed", slug = "cover", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverFileId = image });
        created = await Edit(new { title = "null legacy", slug = "cover", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverUrl = (string?)null });
        Assert.Equal(image, created.GetProperty("coverFileId").GetGuid());
        created = await Edit(new { title = "clear", slug = "cover", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), coverFileId = (Guid?)null, inlineImageIds = Array.Empty<Guid>() });
        Assert.Equal(JsonValueKind.Null, created.GetProperty("coverFileId").ValueKind);
        using var deleted = await PublishingFixture.SendAsync(author, HttpMethod.Delete, $"/api/v1/publications/{id}", created.GetProperty("version").GetGuid(), null, ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var retained = await author.GetAsync($"/api/v1/media/files/{attachment}/content", ct); Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        using var deleteImage = await author.DeleteAsync($"/api/v1/media/files/{image}", ct); Assert.Equal(HttpStatusCode.NoContent, deleteImage.StatusCode);
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        Assert.Empty(await db.PublicationUsages.Where(u => u.PublicationId == id).ToArrayAsync(ct));
        Assert.Empty(await db.MediaRepresentations.Where(r => r.FileId == image).ToArrayAsync(ct));
        Assert.Equal(2, await db.MediaCleanupJobs.CountAsync(j => db.MediaObjects.Any(o => o.Id == j.ObjectId && o.FileId == image), ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachVersusDeleteSerializesBothCommitOrders(bool attachFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new ViewCommandGate(sql => attachFirst
            ? sql.Contains("SELECT * FROM media.files", StringComparison.Ordinal) && sql.Contains("FOR UPDATE", StringComparison.Ordinal)
            : sql.Contains("UPDATE media.files", StringComparison.Ordinal));
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: gate, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var file = await AttachmentAsync(fixture, author, ct);
        var created = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("attach-race"), ct), HttpStatusCode.Created, ct);
        var id = created.GetProperty("id").GetGuid();
        Task<HttpResponseMessage> Attach() => PublishingFixture.SendAsync(author, HttpMethod.Put, $"/api/v1/publications/{id}", created.GetProperty("version").GetGuid(), new
        { title = "attach", slug = "attach-race", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { file } }, ct);
        Task<HttpResponseMessage> Delete() => author.DeleteAsync($"/api/v1/media/files/{file}", ct);
        gate.Arm(); var first = attachFirst ? Attach() : Delete(); Task<HttpResponseMessage>? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            second = attachFirst ? Delete() : Attach();
            await WaitForBlockedWriterAsync(fixture, ct);
        }
        finally { gate.Release.TrySetResult(); }
        using var firstResponse = await first; using var secondResponse = await second!;
        Assert.Equal(attachFirst ? HttpStatusCode.OK : HttpStatusCode.NoContent, firstResponse.StatusCode);
        Assert.Equal(attachFirst ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, secondResponse.StatusCode);
        await using var db = Context(fixture);
        Assert.Equal(attachFirst ? 1 : 0, await db.PublicationUsages.CountAsync(u => u.FileId == file, ct));
        Assert.False(await db.PublicationUsages.AnyAsync(u => db.MediaFiles.Any(f => f.Id == u.FileId && f.State == MediaFileState.Deleted), ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationCommandAndRoleRevocationUseOneTransactionalGuard(bool revokeFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new ViewCommandGate(sql => revokeFirst ? sql.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) : sql.Contains("INSERT INTO media.publication_usages", StringComparison.Ordinal));
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: gate, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var file = await AttachmentAsync(fixture, author, ct);
        var created = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("role-race"), ct), HttpStatusCode.Created, ct);
        var id = created.GetProperty("id").GetGuid(); var owner = created.GetProperty("author").GetProperty("id").GetGuid();
        await using var revoker = Context(fixture); await using var tx = await revoker.Database.BeginTransactionAsync(ct);
        if (revokeFirst)
        {
            await new AccountRoleWriteGuard(revoker).LockAndReadAsync(owner, ct);
            await revoker.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct);
        }
        gate.Arm();
        var command = PublishingFixture.SendAsync(author, HttpMethod.Put, $"/api/v1/publications/{id}", created.GetProperty("version").GetGuid(), new
        { title = "with links", slug = "role-race", markdown = "body", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>(), attachmentIds = new[] { file } }, ct);
        Task? revoke = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (revokeFirst) { await tx.CommitAsync(ct); }
            else { revoke = RevokeAsync(); await WaitForBlockedWriterAsync(fixture, ct); }
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await command;
        Assert.Equal(revokeFirst ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        if (revoke is not null) { await revoke; }
        await using var verify = Context(fixture);
        Assert.Equal(revokeFirst ? 0 : 1, await verify.PublicationUsages.CountAsync(ct));
        Assert.Equal(revokeFirst ? created.GetProperty("title").GetString() : "with links", (await verify.Publications.SingleAsync(ct)).Title);
        async Task RevokeAsync()
        {
            await new AccountRoleWriteGuard(revoker).LockAndReadAsync(owner, ct);
            await revoker.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.role_assignments WHERE user_id={owner}", ct);
            await tx.CommitAsync(ct);
        }
    }

    private static async Task WaitForBlockedWriterAsync(PublishingFixture fixture, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var observer = new NpgsqlConnection(fixture.Database.ConnectionString); await observer.OpenAsync(timeout.Token);
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND cardinality(pg_blocking_pids(pid))>0)", observer);
            if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!) { return; }
            await Task.Yield();
        }
    }

    [Fact]
    public async Task CommandReturnsItsCommittedSnapshotWhenAnotherWriterChangesPublicationBeforeResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("response-snapshot"), ct), HttpStatusCode.Created, ct);
        var id = publication.GetProperty("id").GetGuid(); var version = publication.GetProperty("version").GetGuid();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var delayed = new ApiFactory(connection, fixture.Identity)
        {
            MediaStorage = garage,
            CustomizeServices = services =>
            {
                services.RemoveAll<IPublicationStore>();
                services.AddScoped<IPublicationStore>(p => new AfterCommitStore(ActivatorUtilities.CreateInstance<PublicationStore>(p), committed, release));
            }
        };
        using var client = delayed.CreateClient(); client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        var first = PublishingFixture.SendAsync(client, HttpMethod.Put, $"/api/v1/publications/{id}", version, new
        { title = "first command", slug = "response-snapshot", markdown = "first", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>() }, ct);
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            var current = await PublishingFixture.JsonAsync(await author.GetAsync($"/api/v1/workspace/publications/{id}", ct), HttpStatusCode.OK, ct);
            using var second = await PublishingFixture.SendAsync(author, HttpMethod.Put, $"/api/v1/publications/{id}", current.GetProperty("version").GetGuid(), new
            { title = "second command", slug = "response-snapshot", markdown = "second", categoryIds = Array.Empty<Guid>(), tagIds = Array.Empty<Guid>() }, ct);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }
        finally { release.TrySetResult(); }
        var response = await PublishingFixture.JsonAsync(await first, HttpStatusCode.OK, ct);
        Assert.Equal("first command", response.GetProperty("title").GetString()); Assert.Equal("first", response.GetProperty("markdown").GetString());
        await using var db = Context(fixture); Assert.Equal("second command", (await db.Publications.SingleAsync(ct)).Title);
    }

    private sealed class AfterCommitStore(IPublicationStore inner, TaskCompletionSource committed, TaskCompletionSource release) : IPublicationStore
    {
        public Task<ClassifiedPublication> CreateAsync(CurrentActor actor, PublicationContent content, CancellationToken cancellationToken) => inner.CreateAsync(actor, content, cancellationToken);
        public async Task<ClassifiedPublication?> MutateAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, PublicationCommand command, PublicationContent? content, CancellationToken cancellationToken)
        {
            var result = await inner.MutateAsync(actor, id, readVersion, command, content, cancellationToken);
            committed.TrySetResult(); await release.Task.WaitAsync(cancellationToken); return result;
        }
    }

    [Fact]
    public async Task DeletePreservesQuotaTombstonesAndNeverAcceptsAvatarIds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var admin = await fixture.ActorAsync(LocalRole.Admin, ct);
        using var other = await fixture.ActorAsync(LocalRole.Author, ct);
        var file = await AttachmentAsync(fixture, author, ct);
        await using var db = Context(fixture); var owner = (await db.MediaFiles.SingleAsync(ct)).OwnerId;
        var avatar = MediaFile.Create(owner, MediaPurpose.Avatar, "avatar.png", "image/png", 1, DateTimeOffset.UtcNow);
        var former = MediaFile.Create(owner, MediaPurpose.Avatar, "former.png", "image/png", 1, DateTimeOffset.UtcNow); former.Delete(DateTimeOffset.UtcNow);
        db.MediaFiles.AddRange(avatar, former); await db.SaveChangesAsync(ct);
        foreach (var client in new[] { author, admin, other })
        {
            foreach (var id in new[] { avatar.Id, former.Id })
            { using var hidden = await client.DeleteAsync($"/api/v1/media/files/{id}", ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        }
        using (var hidden = await other.DeleteAsync($"/api/v1/media/files/{file}", ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        using (var removed = await admin.DeleteAsync($"/api/v1/media/files/{file}", ct)) { Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode); }
        foreach (var client in new[] { author, admin })
        { using var replay = await client.DeleteAsync($"/api/v1/media/files/{file}", ct); Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode); }
        using (var hidden = await other.DeleteAsync($"/api/v1/media/files/{file}", ct)) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        db.ChangeTracker.Clear(); Assert.Equal(0, (await db.StorageAllowances.SingleAsync(ct)).UsedBytes);
        Assert.Equal(MediaFileState.Deleted, (await db.MediaFiles.SingleAsync(f => f.Id == file, ct)).State);
        Assert.Single(await db.MediaCleanupJobs.ToArrayAsync(ct));
    }
}
