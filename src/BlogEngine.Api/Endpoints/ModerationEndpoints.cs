using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts;
using BlogEngine.Api.Contracts.Discussions;
using BlogEngine.Api.Contracts.Publishing;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Application;
using BlogEngine.Core.ReadModels;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class ModerationEndpoints
{
    public static void MapModerationEndpoints(this WebApplication app)
    {
        Describe(app.MapGet("/api/v1/moderation/comments", async (HttpRequest request, CurrentActorAccessor actors, ModerationQuery query, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct); AccessPolicy.Demand(actor, ActorAction.ModerateComments);
            var filter = ModerationRequest.Read(request);
            var page = await query.ListAsync(actor, filter.PublicationId, filter.State, PublishingValidation.Page(request), ct);
            return new PageResult<ModerationCommentDto>(page.Items.Select(ModerationCommentDto.From).ToArray(), page.Page, page.PageSize, page.HasNextPage);
        }).WithName("listModerationComments").Produces<PageResult<ModerationCommentDto>>());
        Describe(app.MapPost("/api/v1/comments/{commentId}/hide", async (Guid commentId, HttpRequest request, CurrentActorAccessor actors, ModerationService service, CancellationToken ct) =>
            ModerationCommentDto.From(await service.HideAsync(await actors.GetRequiredAsync(ct), commentId, () => ResourceVersion.Read(request), ct)))
            .WithName("hideComment").Produces<ModerationCommentDto>(), transition: true);
        Describe(app.MapPost("/api/v1/comments/{commentId}/restore", async (Guid commentId, HttpRequest request, CurrentActorAccessor actors, ModerationService service, CancellationToken ct) =>
            ModerationCommentDto.From(await service.RestoreAsync(await actors.GetRequiredAsync(ct), commentId, () => ResourceVersion.Read(request), ct)))
            .WithName("restoreComment").Produces<ModerationCommentDto>(), transition: true);
    }

    private static void Describe(RouteHandlerBuilder route, bool transition = false)
    {
        foreach (var status in new[] { 400, 401, 403, 404, 500, 503 }) { route.ProducesProblem(status); }
        if (transition)
        { foreach (var status in new[] { 409, 413, 415 }) { route.ProducesProblem(status); } }
    }
}
