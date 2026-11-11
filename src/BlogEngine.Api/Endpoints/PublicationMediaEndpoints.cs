using BlogEngine.Api.Configuration;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class PublicationMediaEndpoints
{
    public static void MapPublicationMediaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/publications/{publicationId}/media/{fileId}/content", async (Guid publicationId, Guid fileId, HttpContext context,
            CurrentActorAccessor actors, PublicationMediaAccessQuery query, MediaBinaryTransport binary, CancellationToken ct) =>
        {
            var actor = context.User.Identity?.IsAuthenticated == true ? await actors.GetRequiredAsync(ct) : null;
            var variant = context.Request.Query["variant"];
            if (variant.Count != 1 || variant[0] is not ("attachment" or "display")) { throw DomainError.Validation("variant"); }
            var display = variant[0] == "display";
            await binary.DownloadAsync(context, async token =>
            {
                var content = await query.FindAsync(actor?.Id, publicationId, fileId, display, token);
                return content is null ? null : new MediaDownloadAccess(content.Bucket, content.Key, content.Length, content.Sha256,
                    display ? "image/png" : "application/octet-stream", display ? null : content.Name);
            });
        }).AllowAnonymous().WithMetadata(new OptionalBearerContent(), new BinaryEndpoint("application/octet-stream", AlternateContentType: "image/png")).WithName("downloadPublicationMedia");
    }
}
