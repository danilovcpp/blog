using BlogEngine.Infrastructure.Identity;
using BlogEngine.Infrastructure.Media;

namespace BlogEngine.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous().WithName("healthLive");
        endpoints.MapGet("/health/ready", async (DependencyReadiness readiness, MediaReadiness media, CancellationToken cancellationToken) =>
        {
            await readiness.CheckAsync(cancellationToken);
            await media.CheckAsync(cancellationToken);
            return Results.Ok(new { status = "ready" });
        }).AllowAnonymous().WithName("healthReady");
    }
}
