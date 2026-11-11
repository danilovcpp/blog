using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts;
using BlogEngine.Api.Contracts.Publishing;
using BlogEngine.Core.Publishing.Application;
using BlogEngine.Core.Publishing.Contracts;

namespace BlogEngine.Api.Endpoints;

public static class PublicationCommandEndpoints
{
    public static void MapPublicationCommandEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/publications", async (PublicationWrite body, CurrentActorAccessor actors, PublicationService service, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct);
            var result = await service.CreateAsync(actor, body.ToContent(), ct);
            return TypedResults.Created($"/api/v1/workspace/publications/{result.Publication.Id:D}", WorkspacePublication.Detail(result));
        }).WithName("createPublication");
        app.MapPut("/api/v1/publications/{id}", async (Guid id, PublicationWrite body, HttpRequest request, CurrentActorAccessor actors, PublicationService service, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct);
            var result = await service.MutateAsync(actor, id, () => ResourceVersion.Read(request), PublicationCommand.Edit, body.ToContent(), ct);
            return WorkspacePublication.Detail(result!);
        }).WithName("updatePublication");
        app.MapDelete("/api/v1/publications/{id}", async (Guid id, HttpRequest request, CurrentActorAccessor actors, PublicationService service, CancellationToken ct) =>
        {
            await service.MutateAsync(await actors.GetRequiredAsync(ct), id, () => ResourceVersion.Read(request), PublicationCommand.Delete, null, ct);
            return Results.NoContent();
        }).WithName("deletePublication");
        MapTransition(app, "publish", PublicationCommand.Publish, "publishPublication");
        MapTransition(app, "unpublish", PublicationCommand.Unpublish, "unpublishPublication");
    }
    private static void MapTransition(WebApplication app, string path, PublicationCommand command, string name) =>
        app.MapPost("/api/v1/publications/{id}/" + path, async (Guid id, HttpRequest request, CurrentActorAccessor actors, PublicationService service, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct);
            var result = await service.MutateAsync(actor, id, () => ResourceVersion.Read(request), command, null, ct);
            return WorkspacePublication.Detail(result!);
        }).WithName(name);
}
