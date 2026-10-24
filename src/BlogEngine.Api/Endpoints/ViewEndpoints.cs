using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Engagement;
using BlogEngine.Core.Engagement.Application;
using BlogEngine.Infrastructure.Visitors;

namespace BlogEngine.Api.Endpoints;

public static class ViewEndpoints
{
    public static void MapViewEndpoints(this WebApplication app)
    {
        var route = app.MapPost("/api/v1/publications/{id}/views", async (Guid id, HttpContext http, CurrentActorAccessor actors,
            VisitorIdentityResolver visitors, ViewService service, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.IsAuthenticated == true ? await actors.GetRequiredAsync(ct) : null;
            var header = http.Request.Headers["X-Anonymous-Visitor"];
            var visitor = visitors.Resolve(actor, header.Count == 1 ? header[0] : null);
            var result = await service.RegisterAsync(id, visitor.Identity, ct);
            return new ViewResultDto(result.Counted, result.ViewCount, visitor.AnonymousVisitorId);
        }).AllowAnonymous().WithMetadata(new OptionalBearerContent()).WithName("registerView").Produces<ViewResultDto>();
        foreach (var status in new[] { 400, 401, 404, 413, 415, 500, 503 }) { route.ProducesProblem(status); }
    }
}
