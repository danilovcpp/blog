using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Reading;
using BlogEngine.Core.Engagement.Application;

namespace BlogEngine.Api.Endpoints;

public static class LikeEndpoints
{
    public static void MapLikeEndpoints(this WebApplication app)
    {
        Describe(app.MapPut("/api/v1/publications/{id}/like", async (Guid id, CurrentActorAccessor actors, LikeService service, CancellationToken ct) =>
            CountersDto.From(await service.SetAsync(await actors.GetRequiredAsync(ct), id, true, ct))).WithName("setLike"));
        Describe(app.MapDelete("/api/v1/publications/{id}/like", async (Guid id, CurrentActorAccessor actors, LikeService service, CancellationToken ct) =>
            CountersDto.From(await service.SetAsync(await actors.GetRequiredAsync(ct), id, false, ct))).WithName("removeLike"));
    }

    private static void Describe(RouteHandlerBuilder route)
    {
        route.RequireAuthorization().Produces<CountersDto>();
        foreach (var status in new[] { 400, 401, 403, 404, 413, 415, 500, 503 }) { route.ProducesProblem(status); }
    }
}
