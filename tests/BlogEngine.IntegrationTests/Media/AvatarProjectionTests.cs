using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Discussions.Domain;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Discussions;
using BlogEngine.Infrastructure.Persistence.Publishing;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.IntegrationTests.Media;

public sealed class AvatarProjectionTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    [Fact]
    public async Task MeExposesTheSameEmptyAvatarVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        var me = await PublishingFixture.JsonAsync(await reader.GetAsync("/api/v1/me", ct), HttpStatusCode.OK, ct);
        Assert.True(me.TryGetProperty("avatar", out var avatar)); Assert.Equal(System.Text.Json.JsonValueKind.Null, avatar.ValueKind);
        var state = await AvatarContractTests.StateAsync(reader, ct);
        Assert.Equal(me.GetProperty("avatarVersion").GetGuid(), state.GetProperty("version").GetGuid());
    }

    private static void Author(JsonElement item, Guid? fileId)
    {
        var avatar = item.GetProperty("author").GetProperty("avatar");
        if (fileId is null) { Assert.Equal(JsonValueKind.Null, avatar.ValueKind); return; }
        Assert.Equal(fileId, avatar.GetProperty("fileId").GetGuid());
        Assert.Equal($"/api/v1/avatars/{fileId:D}/256.png", avatar.GetProperty("smallUrl").GetString());
        Assert.Equal($"/api/v1/avatars/{fileId:D}/512.png", avatar.GetProperty("largeUrl").GetString());
    }

    [Fact]
    public async Task EveryAuthorProjectionTracksSwapAndDeleteWithoutBootstrappingPublicUsers()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: capture, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct); using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        using var moderator = await fixture.ActorAsync(LocalRole.Moderator, ct);
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("avatar-projections"), ct), HttpStatusCode.Created, ct);
        Author(publication, null); var id = publication.GetProperty("id").GetGuid();
        publication = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        var comment = await PublishingFixture.JsonAsync(await reader.PostAsJsonAsync($"/api/v1/publications/{id}/comments", new { content = "original" }, ct), HttpStatusCode.Created, ct);
        Author(comment, null); var commentId = comment.GetProperty("id").GetGuid();
        await CheckAsync(null, null);
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        Assert.Equal(0, await db.AvatarStates.CountAsync(ct));
        Guid? authorAvatar = null; Guid? readerAvatar = null;
        for (var i = 0; i < 2; i++)
        {
            authorAvatar = (await AvatarContractTests.AssignAsync(fixture, author, ct)).GetProperty("fileId").GetGuid();
            readerAvatar = (await AvatarContractTests.AssignAsync(fixture, reader, ct)).GetProperty("fileId").GetGuid();
            await CheckAsync(authorAvatar, readerAvatar);
            publication = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Put, $"/api/v1/publications/{id}", publication.GetProperty("version").GetGuid(), PublishingFixture.Content("avatar-projections"), ct), HttpStatusCode.OK, ct);
            Author(publication, authorAvatar);
            foreach (var transition in new[] { "unpublish", "publish" })
            {
                publication = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/{transition}", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
                Author(publication, authorAvatar);
            }
            comment = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(reader, HttpMethod.Put, $"/api/v1/comments/{commentId}", comment.GetProperty("version").GetGuid(), new { content = $"edit {i}" }, ct), HttpStatusCode.OK, ct);
            Author(comment, readerAvatar);
        }
        // A public placeholder never discloses the author or its avatar, even with visible descendants.
        var child = await PublishingFixture.JsonAsync(await reader.PostAsJsonAsync($"/api/v1/publications/{id}/comments", new { parentId = commentId, content = "reply" }, ct), HttpStatusCode.Created, ct);
        Author(child, readerAvatar);
        comment = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(moderator, HttpMethod.Post, $"/api/v1/comments/{commentId}/hide", comment.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        Author(comment, readerAvatar);
        var tree = await PublishingFixture.JsonAsync(await fixture.Anonymous.GetAsync($"/api/v1/publications/{id}/comments", ct), HttpStatusCode.OK, ct);
        Assert.Equal(JsonValueKind.Null, tree.GetProperty("items")[0].GetProperty("author").ValueKind);
        comment = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(moderator, HttpMethod.Post, $"/api/v1/comments/{commentId}/restore", comment.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        Author(comment, readerAvatar);
        using (var deleted = await PublishingFixture.SendAsync(reader, HttpMethod.Delete, $"/api/v1/comments/{commentId}", comment.GetProperty("version").GetGuid(), null, ct)) { Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
        tree = await PublishingFixture.JsonAsync(await fixture.Anonymous.GetAsync($"/api/v1/publications/{id}/comments", ct), HttpStatusCode.OK, ct);
        Assert.Equal(JsonValueKind.Null, tree.GetProperty("items")[0].GetProperty("author").ValueKind);
        foreach (var client in new[] { author, reader })
        {
            var state = await AvatarContractTests.StateAsync(client, ct);
            using var deleted = await PublishingFixture.SendAsync(client, HttpMethod.Delete, "/api/v1/me/avatar", state.GetProperty("version").GetGuid(), null, ct);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }
        Author(await PublishingFixture.JsonAsync(await fixture.Anonymous.GetAsync($"/api/v1/publications/{id}", ct), HttpStatusCode.OK, ct), null);
        Author(await PublishingFixture.JsonAsync(await reader.GetAsync($"/api/v1/comments/{child.GetProperty("id").GetGuid()}", ct), HttpStatusCode.OK, ct), null);

        async Task CheckAsync(Guid? publicationAvatar, Guid? commentAvatar)
        {
            foreach (var path in new[] { $"/api/v1/publications/{id}", "/api/v1/publications/by-slug/avatar-projections" })
            { Author(await PublishingFixture.JsonAsync(await fixture.Anonymous.GetAsync(path, ct), HttpStatusCode.OK, ct), publicationAvatar); }
            foreach (var path in new[] { $"/api/v1/workspace/publications/{id}" })
            { Author(await PublishingFixture.JsonAsync(await author.GetAsync(path, ct), HttpStatusCode.OK, ct), publicationAvatar); }
            foreach (var (client, path) in new[] { (fixture.Anonymous, "/api/v1/publications"), (author, "/api/v1/workspace/publications") })
            {
                var page = await PublishingFixture.JsonAsync(await client.GetAsync(path, ct), HttpStatusCode.OK, ct);
                Assert.All(page.GetProperty("items").EnumerateArray(), item => Author(item, publicationAvatar));
            }
            Author(await PublishingFixture.JsonAsync(await reader.GetAsync($"/api/v1/comments/{commentId}", ct), HttpStatusCode.OK, ct), commentAvatar);
            foreach (var (client, path) in new[] { (fixture.Anonymous, $"/api/v1/publications/{id}/comments"), (moderator, "/api/v1/moderation/comments") })
            {
                var page = await PublishingFixture.JsonAsync(await client.GetAsync(path, ct), HttpStatusCode.OK, ct);
                Assert.All(page.GetProperty("items").EnumerateArray(), item => Author(item, commentAvatar));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandSnapshotKeepsAvatarAndContentWhenAnotherCommitPrecedesResponse(bool commentCommand)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var firstAvatar = (await AvatarContractTests.AssignAsync(fixture, author, ct)).GetProperty("fileId").GetGuid();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = fixture.Factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString;
        await using var delayed = new ApiFactory(connection, fixture.Identity)
        {
            MediaStorage = garage,
            CustomizeServices = services =>
            {
                if (commentCommand)
                {
                    services.RemoveAll<ICommentWriteStore>();
                    services.AddScoped<ICommentWriteStore>(p => new DelayedCommentStore(ActivatorUtilities.CreateInstance<CommentWriteStore>(p), committed, release));
                }
                else
                {
                    services.RemoveAll<IPublicationStore>();
                    services.AddScoped<IPublicationStore>(p => new DelayedPublicationStore(ActivatorUtilities.CreateInstance<PublicationStore>(p), committed, release));
                }
            }
        };
        using var client = delayed.CreateClient(); client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        Task<HttpResponseMessage> responseTask;
        if (commentCommand)
        {
            var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("snapshot-comments"), ct), HttpStatusCode.Created, ct);
            var id = publication.GetProperty("id").GetGuid();
            _ = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
            responseTask = client.PostAsJsonAsync($"/api/v1/publications/{id}/comments", new { content = "snapshot text" }, ct);
        }
        else { responseTask = client.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("snapshot-publication", "snapshot text"), ct); }
        Guid secondAvatar;
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            secondAvatar = (await AvatarContractTests.AssignAsync(fixture, author, ct)).GetProperty("fileId").GetGuid();
        }
        finally { release.TrySetResult(); }
        var response = await PublishingFixture.JsonAsync(await responseTask, HttpStatusCode.Created, ct);
        Author(response, firstAvatar); Assert.Equal("snapshot text", response.GetProperty(commentCommand ? "content" : "markdown").GetString());
        var path = commentCommand ? $"/api/v1/comments/{response.GetProperty("id").GetGuid()}" : $"/api/v1/workspace/publications/{response.GetProperty("id").GetGuid()}";
        Author(await PublishingFixture.JsonAsync(await author.GetAsync(path, ct), HttpStatusCode.OK, ct), secondAvatar);
    }

    [Fact]
    public async Task AvatarBatchHasOneBoundedQueryForOneOrHundredAuthorsAndDoesNotCreateStates()
    {
        var ct = TestContext.Current.CancellationToken; var capture = new ReadingSqlCapture();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: capture, mediaStorage: garage);
        using var reader = await fixture.ActorAsync(LocalRole.Reader, ct);
        var fileId = (await AvatarContractTests.AssignAsync(fixture, reader, ct)).GetProperty("fileId").GetGuid();
        var owner = (await PublishingFixture.JsonAsync(await reader.GetAsync("/api/v1/me", ct), HttpStatusCode.OK, ct)).GetProperty("id").GetGuid();
        using var scope = fixture.Factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var avatars = scope.ServiceProvider.GetRequiredService<IAuthorAvatarReader>();
        var evidence = new List<object>();
        foreach (var size in new[] { 1, 100 })
        {
            var ids = new[] { owner }.Concat(Enumerable.Range(1, size - 1).Select(_ => Guid.NewGuid())).ToArray();
            capture.Clear(); var result = await avatars.ReadAsync(ids, ct);
            Assert.Equal(size, result.Count); Assert.Equal(fileId, result[owner]!.FileId);
            Assert.All(result.Where(p => p.Key != owner), p => Assert.Null(p.Value));
            var command = Assert.Single(capture.Commands);
            Assert.DoesNotContain("INSERT", command.Sql, StringComparison.Ordinal);
            await using var connection = new NpgsqlConnection(fixture.Database.ConnectionString); await connection.OpenAsync(ct);
            await using var explain = QueryInspectionTests.Command(connection, command, "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) ");
            evidence.Add(new { Authors = size, DataQueries = 1, command.Sql, Plan = JsonSerializer.Deserialize<JsonElement>((string)(await explain.ExecuteScalarAsync(ct))!) });
        }
        Assert.Equal(1, await db.AvatarStates.CountAsync(ct));
        await File.WriteAllTextAsync(PublishingFixture.ArtifactPath("media/phase5/avatar-query.json"), JsonSerializer.Serialize(evidence), ct);
    }

    [Fact]
    public async Task FeedKeepsItsReadSnapshotWhenAvatarChangesBetweenPageAndBatch()
    {
        var ct = TestContext.Current.CancellationToken; var gate = new AvatarBatchGate();
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, commandInterceptor: gate, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var old = (await AvatarContractTests.AssignAsync(fixture, author, ct)).GetProperty("fileId").GetGuid();
        var publication = await PublishingFixture.JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content("feed-snapshot"), ct), HttpStatusCode.Created, ct);
        var id = publication.GetProperty("id").GetGuid();
        _ = await PublishingFixture.JsonAsync(await PublishingFixture.SendAsync(author, HttpMethod.Post, $"/api/v1/publications/{id}/publish", publication.GetProperty("version").GetGuid(), null, ct), HttpStatusCode.OK, ct);
        gate.Arm(); var pageTask = fixture.Anonymous.GetAsync("/api/v1/publications", ct); Guid next;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            next = (await AvatarContractTests.AssignAsync(fixture, author, ct)).GetProperty("fileId").GetGuid();
        }
        finally { gate.Release.TrySetResult(); }
        var page = await PublishingFixture.JsonAsync(await pageTask, HttpStatusCode.OK, ct);
        Author(page.GetProperty("items")[0], old);
        var current = await PublishingFixture.JsonAsync(await fixture.Anonymous.GetAsync("/api/v1/publications", ct), HttpStatusCode.OK, ct);
        Author(current.GetProperty("items")[0], next);
    }

    private sealed class AvatarBatchGate : DbCommandInterceptor
    {
        private int armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM media.avatar_states", StringComparison.Ordinal) && Interlocked.Exchange(ref armed, 0) == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private sealed class DelayedPublicationStore(IPublicationStore inner, TaskCompletionSource committed, TaskCompletionSource release) : IPublicationStore
    {
        public async Task<ClassifiedPublication> CreateAsync(CurrentActor actor, PublicationContent content, CancellationToken ct)
        { var result = await inner.CreateAsync(actor, content, ct); committed.TrySetResult(); await release.Task.WaitAsync(ct); return result; }
        public Task<ClassifiedPublication?> MutateAsync(CurrentActor actor, Guid id, Func<Guid> readVersion, PublicationCommand command, PublicationContent? content, CancellationToken ct) =>
            inner.MutateAsync(actor, id, readVersion, command, content, ct);
    }
    private sealed class DelayedCommentStore(ICommentWriteStore inner, TaskCompletionSource committed, TaskCompletionSource release) : ICommentWriteStore
    {
        public async Task<CommentSnapshot> CreateAsync(Guid publicationId, Guid? parentId, Func<Comment?, DateTimeOffset, Comment> create, CancellationToken ct)
        { var result = await inner.CreateAsync(publicationId, parentId, create, ct); committed.TrySetResult(); await release.Task.WaitAsync(ct); return result; }
        public Task<CommentSnapshot> MutateAsync(Guid id, Action<Comment, DateTimeOffset> mutation, CancellationToken ct) => inner.MutateAsync(id, mutation, ct);
    }
}
