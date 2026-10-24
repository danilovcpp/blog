using BlogEngine.Infrastructure.Identity;

namespace BlogEngine.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "healthy" })).AllowAnonymous().WithName("healthLive");
        endpoints.MapGet("/health/ready", async (DependencyReadiness readiness, CancellationToken cancellationToken) =>
        {
            await readiness.CheckAsync(cancellationToken);
            return Results.Ok(new { status = "healthy" });
        }).AllowAnonymous().WithName("healthReady");
    }
}
