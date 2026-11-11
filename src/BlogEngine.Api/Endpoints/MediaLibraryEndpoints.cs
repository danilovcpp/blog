using System.Globalization;
using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Media;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class MediaLibraryEndpoints
{
    public static void MapMediaLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/media/files", async (HttpRequest request, CurrentActorAccessor actors, MediaLibrary service, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct);
            Guid? ownerId = null;
            if (request.Query.TryGetValue("ownerId", out var owners))
            {
                if (owners.Count != 1 || !Guid.TryParseExact(owners[0], "D", out var parsed)) { throw DomainError.Validation("ownerId"); }
                ownerId = parsed;
            }
            return FilePageDto.From(await service.ListAsync(actor, ownerId, PageValue(request, "page", 1), PageValue(request, "pageSize", 20), ct));
        }).WithName("listMediaFiles");

        app.MapGet("/api/v1/media/files/{id}", async (Guid id, CurrentActorAccessor actors, MediaLibrary service, CancellationToken ct) =>
            FileMetadataDto.From(await service.GetAsync(await actors.GetRequiredAsync(ct), id, ct))).WithName("getMediaFile");

        app.MapDelete("/api/v1/media/files/{id}", async (Guid id, CurrentActorAccessor actors, FileDeletion service, CancellationToken ct) =>
        {
            await service.DeleteAsync(await actors.GetRequiredAsync(ct), id, ct);
            return Results.NoContent();
        }).WithName("deleteMediaFile");

        app.MapGet("/api/v1/media/files/{id}/content", async (Guid id, HttpContext context, CurrentActorAccessor actors, MediaLibrary service, PublicationImageQuery images, MediaBinaryTransport binary, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct);
            var variant = context.Request.Query["variant"];
            if (variant.Count > 1 || (variant.Count == 1 && variant[0] is not ("original" or "display"))) { throw DomainError.Validation("variant"); }
            var display = variant.Count == 1 && variant[0] == "display";
            await binary.DownloadAsync(context, async token =>
            {
                var content = display ? await images.DisplayAsync(actor.Id, id, token) : await service.ContentAsync(actor, id, token);
                return content is null ? null : new MediaDownloadAccess(content.Bucket, content.Key, content.Length, content.Sha256, display ? "image/png" : "application/octet-stream", display ? null : content.Name);
            });
        }).WithMetadata(new BinaryEndpoint("application/octet-stream", AlternateContentType: "image/png")).WithName("downloadPrivateMediaFile");
    }

    private static int PageValue(HttpRequest request, string name, int fallback)
    {
        if (!request.Query.TryGetValue(name, out var values)) { return fallback; }
        if (values.Count != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
        { throw DomainError.Validation(name); }
        return value;
    }
}
