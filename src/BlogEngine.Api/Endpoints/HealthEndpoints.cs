using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Identity;
using BlogEngine.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace BlogEngine.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous().WithName("healthLive");
        endpoints.MapGet("/health/ready", async (DependencyReadiness readiness, IMediaSpool spool, IOptions<MediaOptions> media, CancellationToken cancellationToken) =>
        {
            if (!await spool.CheckReadinessAsync(cancellationToken) || media.Value.MaintenanceMode) { throw new DomainError("dependency_unavailable"); }
            await readiness.CheckAsync(cancellationToken);
            return Results.Ok(new { status = "ready" });
        }).AllowAnonymous().WithName("healthReady");
    }
}
