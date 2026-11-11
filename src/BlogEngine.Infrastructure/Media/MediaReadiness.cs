using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SkiaSharp;

namespace BlogEngine.Infrastructure.Media;

public sealed class MediaReadiness(BlogDbContext db, IMediaObjectStorage storage, IMediaSpool spool, IOptions<MediaOptions> options, ILogger<MediaReadiness> logger)
{
    public const string SentinelKey = "_blog/health-v1";
    public const long SentinelLength = 29;
    public const string SentinelSha256 = "e85f6fe5e6589eee2f0cf83bc9356352a1a94ef01ff75dc925b6487fe34bcf75";

    public async Task CheckAsync(CancellationToken ct)
    {
        if (options.Value.MaintenanceMode) { MediaTelemetry.ReadinessFailures.Add(1); throw new DomainError("dependency_unavailable"); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.Storage.ReadinessTimeout);
        try
        {
            var schema = await db.Database.SqlQueryRaw<bool>("""
                SELECT (EXISTS(SELECT 1 FROM media.schema_version WHERE version=1)
                    AND (SELECT bool_and(has_table_privilege(current_user, 'media.' || name, 'SELECT'))
                         FROM unnest(ARRAY['files','uploads','allowances','avatar_states','objects','representations','cleanup_jobs','publication_usages']) AS name)) AS "Value"
                """).SingleAsync(timeout.Token);
            if (!schema || !await spool.CheckReadinessAsync(timeout.Token)) { throw new DomainError("dependency_unavailable"); }
            using var codec = new SKBitmap(1, 1);
            foreach (var bucket in new[] { MediaBucketKind.Originals, MediaBucketKind.Renditions })
            {
                await using var sentinel = await storage.GetVerifiedAsync(bucket, SentinelKey, SentinelLength, Convert.FromHexString(SentinelSha256), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is MediaStorageException or DomainError or TypeInitializationException or DllNotFoundException or OperationCanceledException or NpgsqlException || DatabaseErrorMapper.Map(error) is not null)
        {
            MediaTelemetry.ReadinessFailures.Add(1);
            logger.LogWarning("Media readiness failed with {ExceptionType} code {Code}", error.GetType().Name, "dependency_unavailable");
            throw new DomainError("dependency_unavailable");
        }
    }
}
