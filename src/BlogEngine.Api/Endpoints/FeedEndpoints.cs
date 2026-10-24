using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Reading;
using BlogEngine.Core.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class FeedEndpoints
{
    public static void MapFeedEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/publications", async (HttpContext http, CurrentActorAccessor actors, IPublicFeedReader query, CancellationToken ct) =>
        {
            var request = FeedRequest.Parse(http.Request);
            var actorId = http.User.Identity?.IsAuthenticated == true ? (await actors.GetRequiredAsync(ct)).Id : (Guid?)null;
            var page = await query.ReadAsync(request, actorId, ct);
            return new PageResult<PublicationCardDto>(page.Items.Select(PublicationCardDto.From).ToArray(), page.Page, page.PageSize, page.HasNextPage);
        }).AllowAnonymous().WithMetadata(new OptionalBearerContent()).WithName("listPublications");
    }
}
