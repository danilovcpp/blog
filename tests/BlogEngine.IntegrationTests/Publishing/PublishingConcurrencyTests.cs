using System.Net;
using System.Net.Http.Json;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Infrastructure.Persistence.Publishing;
using BlogEngine.IntegrationTests.Accounts;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Publishing;

[Trait("Category", "Publishing")]
[Trait("Category", "Concurrency")]
public sealed class PublishingConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ConcurrentSlugAndEditHaveOneWinnerAndLosingReplacementLeavesNoPartialJoins()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct);
        var barrier = new ConcurrencyBarrier(2);
        var creations = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await barrier.SignalAndWaitAsync(ct);
            return await author.PostAsJsonAsync("/api/v1/publications", Content(), ct);
        }));
        Assert.Single(creations, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(creations, x => x.StatusCode == HttpStatusCode.Conflict);
        var created = await JsonAsync(creations.Single(x => x.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created, ct);
        creations.Single(x => x.StatusCode == HttpStatusCode.Conflict).Dispose();
        var id = created.GetProperty("id").GetGuid(); var version = created.GetProperty("version").GetGuid(); var path = "/api/v1/publications/" + id;
        barrier = new ConcurrencyBarrier(2);
        var edits = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            await barrier.SignalAndWaitAsync(ct);
            return await SendAsync(author, HttpMethod.Put, path, version, Content(markdown: "edit-" + i), ct);
        }));
        Assert.Single(edits, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(edits, x => x.StatusCode == HttpStatusCode.Conflict);
        var edited = await JsonAsync(edits.Single(x => x.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK, ct);
        edits.Single(x => x.StatusCode == HttpStatusCode.Conflict).Dispose();
        var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var category = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/categories", new { name = "N", slug = "n" }, ct), HttpStatusCode.Created, ct);
        var assigned = await JsonAsync(await SendAsync(author, HttpMethod.Put, path, edited.GetProperty("version").GetGuid(), Content(categories: [category.GetProperty("id").GetGuid()]), ct), HttpStatusCode.OK, ct);
        using (var invalid = await SendAsync(author, HttpMethod.Put, path, assigned.GetProperty("version").GetGuid(), Content(markdown: "must-rollback", tags: [Guid.NewGuid()]), ct))
        { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        var after = await JsonAsync(await author.GetAsync("/api/v1/workspace/publications/" + id, ct), HttpStatusCode.OK, ct);
        Assert.Equal(assigned.GetProperty("version").GetGuid(), after.GetProperty("version").GetGuid()); Assert.Single(after.GetProperty("categories").EnumerateArray());
        // A conflicting slug is discovered only during SaveChanges, after joins were replaced in the transaction.
        await JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", Content(slug: "reserved"), ct), HttpStatusCode.Created, ct);
        using (var conflict = await SendAsync(author, HttpMethod.Put, path, after.GetProperty("version").GetGuid(), Content(slug: "reserved"), ct)) { Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); }
        var rolledBack = await JsonAsync(await author.GetAsync("/api/v1/workspace/publications/" + id, ct), HttpStatusCode.OK, ct);
        Assert.Equal(after.GetRawText(), rolledBack.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AssignmentAndDeleteBothLockOrdersAreProtectedByForeignKeys(bool assignmentFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20)); ct = timeout.Token;
        await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct);
        var admin = await f.ActorAsync(LocalRole.Admin, ct);
        var draft = await JsonAsync(await author.PostAsJsonAsync("/api/v1/publications", Content(), ct), HttpStatusCode.Created, ct);
        var category = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/categories", new { name = "N", slug = "n" }, ct), HttpStatusCode.Created, ct);
        var pubId = draft.GetProperty("id").GetGuid(); var categoryId = category.GetProperty("id").GetGuid();
        await using var first = new NpgsqlConnection(f.Database.ConnectionString); await first.OpenAsync(ct);
        await using var tx = await first.BeginTransactionAsync(ct);
        var sql = assignmentFirst ? "INSERT INTO publishing.publication_categories VALUES (@p,@c)" : "DELETE FROM publishing.categories WHERE id=@c";
        await using var command = new NpgsqlCommand(sql, first, tx); command.Parameters.AddWithValue("p", pubId); command.Parameters.AddWithValue("c", categoryId);
        await command.ExecuteNonQueryAsync(ct);
        // The second transaction reaches its FK/row lock while the winner remains uncommitted.
        await using var secondDb = BootstrapTests.Context(f.Database.ConnectionString);
        Task second;
        if (assignmentFirst)
        {
            second = new TaxonomyStore(secondDb).MutateAsync(TaxonomyKind.Category, categoryId,
                () => category.GetProperty("version").GetGuid(), null, ct);
        }
        else
        {
            second = new PublicationStore(secondDb, new PublicationWriteScope(secondDb)).MutateAsync(
                new CurrentActor(draft.GetProperty("author").GetProperty("id").GetGuid(), [LocalRole.Reader, LocalRole.Author]), pubId,
                () => draft.GetProperty("version").GetGuid(), PublicationCommand.Edit, new("Changed", "hello", "changed", null, null, [categoryId], []), ct);
        }
        // Opening a separate connection and sending a marker through the same server does not prove a wait;
        // pg_blocking_pids explicitly acknowledges that the contender has reached the winner's lock.
        await using var observer = new NpgsqlConnection(f.Database.ConnectionString); await observer.OpenAsync(ct);
        while (true)
        {
            await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND cardinality(pg_blocking_pids(pid)) > 0)", observer);
            if ((bool)(await waiting.ExecuteScalarAsync(ct))!) { break; }
            if (second.IsCompleted) { await second; Assert.Fail("The contender must wait for the uncommitted winner."); }
            await Task.Yield();
        }
        await tx.CommitAsync(ct);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => second);
        Assert.Equal("taxonomy_in_use", BlogEngine.Infrastructure.Persistence.DatabaseErrorMapper.Map(error)?.Code);
        await using var verify = BootstrapTests.Context(f.Database.ConnectionString);
        Assert.Equal(assignmentFirst ? 1 : 0, await verify.PublicationCategories.CountAsync(ct));
        var publication = await verify.Publications.SingleAsync(ct);
        Assert.Equal(draft.GetProperty("version").GetGuid(), publication.Version);
        Assert.Equal(draft.GetProperty("markdown").GetString(), publication.Markdown);
    }
}
