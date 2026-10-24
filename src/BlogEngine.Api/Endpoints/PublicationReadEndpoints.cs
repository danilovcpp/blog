using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Reading;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class PublicationReadEndpoints
{
    public static void MapPublicationReadEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/publications/{id}", async (Guid id, HttpContext http, CurrentActorAccessor actors, PublicationDetailQuery query, CancellationToken ct) =>
            PublicationDto.From(await query.GetAsync(id, null, http.User.Identity?.IsAuthenticated == true ? (await actors.GetRequiredAsync(ct)).Id : null, ct)))
            .AllowAnonymous().WithMetadata(new OptionalBearerContent()).WithName("getPublication");
        app.MapGet("/api/v1/publications/by-slug/{slug}", async (string slug, HttpContext http, CurrentActorAccessor actors, PublicationDetailQuery query, CancellationToken ct) =>
            PublicationDto.From(await query.GetAsync(null, slug, http.User.Identity?.IsAuthenticated == true ? (await actors.GetRequiredAsync(ct)).Id : null, ct)))
            .AllowAnonymous().WithMetadata(new OptionalBearerContent()).WithName("getPublicationBySlug");
    }
}
