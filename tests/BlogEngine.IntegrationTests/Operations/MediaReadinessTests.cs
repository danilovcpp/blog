using System.Net;
using System.Net.Http.Json;
using BlogEngine.Api.Configuration;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlogEngine.IntegrationTests.Operations;

public sealed class MediaReadinessTests(PostgresFixture postgres, GarageFixture garage) : IClassFixture<PostgresFixture>, IClassFixture<GarageFixture>
{
    [Fact]
    public async Task ReadinessRequiresMediaMarkerAndGrantsWhileLivenessChecksOnlyProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var ready = await fixture.Anonymous.GetAsync("/health/ready", ct); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM media.schema_version", ct);
        using var failed = await fixture.Anonymous.GetAsync("/health/ready", ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var body = await failed.Content.ReadAsStringAsync(ct); Assert.DoesNotContain("schema_version", body, StringComparison.Ordinal);
        using var live = await fixture.Anonymous.GetAsync("/health/live", ct); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task MaintenanceBlocksMutationsAndClaimsButRetainsLiveness()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var author = await fixture.ActorAsync(LocalRole.Author, ct);
        var options = fixture.Factory.Services.GetRequiredService<IOptions<MediaOptions>>().Value;
        options.MaintenanceMode = true;
        using var response = await author.PostAsJsonAsync("/api/v1/publications", PublishingFixture.Content(), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.NotNull(response.Headers.RetryAfter);
        using var ready = await fixture.Anonymous.GetAsync("/health/ready", ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var live = await fixture.Anonymous.GetAsync("/health/live", ct); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        var cycle = await fixture.Factory.Services.GetRequiredService<BlogEngine.Infrastructure.Maintenance.MediaCleanupCycle>().RunAsync(ct);
        Assert.Equal((0, 0, 0), cycle);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<BlogEngine.Core.Media.Contracts.IMediaProcessingStore>().DueAsync(ct));
    }

    [Fact]
    public async Task MissingSchemaAndRuntimeReadGrantAreSafe503WithoutRepair()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options);
        var role = fixture.Roles["runtime_role"];
        Assert.All(role, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '_'));
        await using var admin = new NpgsqlConnection(fixture.Database.ConnectionString); await admin.OpenAsync(ct);
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(role);
        await using var revoke = new NpgsqlCommand("REVOKE SELECT ON media.objects FROM " + quoted, admin); await revoke.ExecuteNonQueryAsync(ct);
        using (var response = await fixture.Anonymous.GetAsync("/health/ready", ct)) { Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); }
        await using var grant = new NpgsqlCommand("GRANT SELECT ON media.objects TO " + quoted, admin); await grant.ExecuteNonQueryAsync(ct);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE media.schema_version RENAME TO unavailable_marker", ct);
        using var failed = await fixture.Anonymous.GetAsync("/health/ready", ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain(role, await failed.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_class WHERE oid=to_regclass('media.schema_version')").SingleAsync(ct));
    }

    [Fact]
    public async Task MissingSentinelFailsWithoutWritingAndRecoversAfterExplicitProvisioning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        using var s3 = garage.CreateClient();
        await s3.DeleteObjectAsync(garage.RenditionsBucket, GarageFixture.SentinelKey, ct);
        try
        {
            using var failed = await fixture.Anonymous.GetAsync("/health/ready", ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            using var live = await fixture.Anonymous.GetAsync("/health/live", ct); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            var text = await failed.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain(garage.Endpoint.AbsoluteUri, text, StringComparison.Ordinal);
            Assert.DoesNotContain(garage.Configuration["Media:Storage:SecretKey"]!, string.Join('\n', fixture.Factory.Logs.Entries.Select(e => e.Message)), StringComparison.Ordinal);
            await Assert.ThrowsAnyAsync<Amazon.S3.AmazonS3Exception>(() => s3.GetObjectAsync(garage.RenditionsBucket, GarageFixture.SentinelKey, ct));
        }
        finally { await garage.ProvisionAsync(ct); }
        using var recovered = await fixture.Anonymous.GetAsync("/health/ready", ct); Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpoolOwnershipConflictOrPreparationFailureKeepsLiveness(bool invalidDirectory)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PublishingFixture.StartAsync(postgres, ct, mediaStorage: garage);
        var directory = fixture.Factory.Services.GetRequiredService<IOptions<MediaOptions>>().Value.Spool.Directory;
        var path = invalidDirectory ? Path.Combine(directory, "not-a-directory") : directory;
        if (invalidDirectory) { await File.WriteAllTextAsync(path, "fixture", ct); }
        await using var second = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Media:Spool:Directory"] = path })));
        using var client = second.CreateClient();
        using var failed = await client.GetAsync("/health/ready", ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using var live = await client.GetAsync("/health/live", ct); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
