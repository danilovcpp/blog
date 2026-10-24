using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts.Publishing;
using BlogEngine.Core.ReadModels;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/workspace/publications", async (HttpRequest request, CurrentActorAccessor actors, WorkspaceQueries queries, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct);
            var page = await queries.ListAsync(actor, PublishingValidation.Page(request), PublishingValidation.State(request), ct);
            return new PageResult<WorkspaceCard>(page.Items.Select(WorkspaceCard.From).ToArray(), page.Page, page.PageSize, page.HasNextPage);
        }).WithName("listWorkspacePublications");
        app.MapGet("/api/v1/workspace/publications/{id}", async (Guid id, CurrentActorAccessor actors, WorkspaceQueries queries, CancellationToken ct) =>
            WorkspacePublication.Detail(await queries.GetAsync(await actors.GetRequiredAsync(ct), id, ct))).WithName("getWorkspacePublication");
    }
}
