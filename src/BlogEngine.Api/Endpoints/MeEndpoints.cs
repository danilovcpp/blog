using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Accounts;

namespace BlogEngine.Api.Endpoints;

public static class MeEndpoints
{
    public static void MapMeEndpoints(this WebApplication app) =>
        app.MapGet("/api/v1/me", async (CurrentActorAccessor actors, CancellationToken cancellationToken) =>
        {
            var actor = await actors.GetRequiredAsync(cancellationToken);
            return new MeDto(actor.Id, actor.Roles.Select(role => role.ToString()).ToArray());
        }).RequireAuthorization().WithName("getMe");
}
