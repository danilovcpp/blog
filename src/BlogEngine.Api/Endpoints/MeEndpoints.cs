using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Accounts;
using BlogEngine.Core.Media.Application;

namespace BlogEngine.Api.Endpoints;

public static class MeEndpoints
{
    public static void MapMeEndpoints(this WebApplication app) =>
        app.MapGet("/api/v1/me", async (CurrentActorAccessor actors, AvatarService avatars, CancellationToken cancellationToken) =>
        {
            var actor = await actors.GetRequiredAsync(cancellationToken);
            var profile = await avatars.ProfileAsync(actor, cancellationToken);
            return new MeDto(actor.Id, profile.Actor.Roles.Select(role => role.ToString()).ToArray(), profile.State.Avatar, profile.State.Version);
        }).RequireAuthorization().WithName("getMe");
}
