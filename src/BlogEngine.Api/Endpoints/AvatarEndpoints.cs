using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts;
using BlogEngine.Api.Contracts.Media;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class AvatarEndpoints
{
    public static void MapAvatarEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/me/avatar", async (CurrentActorAccessor actors, AvatarService service, CancellationToken ct) =>
            AvatarStateDto.From((await service.ProfileAsync(await actors.GetRequiredAsync(ct), ct)).State)).WithName("getOwnAvatarState");
        app.MapDelete("/api/v1/me/avatar", async (HttpRequest request, CurrentActorAccessor actors, AvatarService service, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct);
            if (request.Query.Count != 0) { throw DomainError.Validation("query"); }
            return AvatarStateDto.From(await service.DeleteAsync(actor, ResourceVersion.Read(request), ct));
        }).WithName("deleteOwnAvatar");
        app.MapGet("/api/v1/avatars/{fileId}/{size}.png", async (Guid fileId, int size, HttpContext context,
            AvatarContentQuery query, MediaBinaryTransport binary) =>
        {
            if (size is not (256 or 512)) { throw new DomainError("resource_not_found"); }
            await binary.DownloadAsync(context, async token =>
            {
                var content = await query.FindAsync(fileId, size, token);
                return content is null ? null : new MediaDownloadAccess(content.Bucket, content.Key, content.Length, content.Sha256, "image/png");
            });
        }).AllowAnonymous().WithMetadata(new OptionalBearerContent(), new BinaryEndpoint("image/png")).WithName("getPublicAvatarImage");
    }
}
