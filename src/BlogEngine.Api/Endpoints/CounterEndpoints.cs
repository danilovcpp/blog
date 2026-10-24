using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Reading;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class CounterEndpoints
{
    public static void MapCounterEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/publications/{id}/counters", async (Guid id, HttpContext http, CurrentActorAccessor actors, PublicationCountersQuery query, CancellationToken ct) =>
            CountersDto.From(await query.GetAsync(id, http.User.Identity?.IsAuthenticated == true ? (await actors.GetRequiredAsync(ct)).Id : null, ct)))
            .AllowAnonymous().WithMetadata(new OptionalBearerContent()).WithName("getCounters");
    }
}
