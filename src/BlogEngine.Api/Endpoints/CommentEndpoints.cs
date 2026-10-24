using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts;
using BlogEngine.Api.Contracts.Discussions;
using BlogEngine.Api.Contracts.Publishing;
using BlogEngine.Core.Discussions.Application;
using BlogEngine.Core.ReadModels;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class CommentEndpoints
{
    public static void MapCommentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/publications/{id}/comments", async (Guid id, HttpRequest request, ICommentReader reader, CancellationToken ct) =>
        {
            var page = await reader.ReadAsync(id, CommentQuery.ParentId(request), PublishingValidation.Page(request), ct);
            return new PageResult<PublicCommentDto>(page.Items.Select(PublicCommentDto.From).ToArray(), page.Page, page.PageSize, page.HasNextPage);
        }).AllowAnonymous().WithMetadata(new OptionalBearerContent()).WithName("listComments");
        app.MapPost("/api/v1/publications/{id}/comments", async (Guid id, CommentCreate body, CurrentActorAccessor actors, CommentService service, CancellationToken ct) =>
        {
            var comment = await service.CreateAsync(await actors.GetRequiredAsync(ct), id, body.ParentId, body.Content, ct);
            return Results.Created($"/api/v1/comments/{comment.Id:D}", CommentEditContextDto.From(comment));
        }).Produces<CommentEditContextDto>(StatusCodes.Status201Created).WithName("createComment");
        app.MapGet("/api/v1/comments/{commentId}", async (Guid commentId, CurrentActorAccessor actors, CommentEditQuery query, CancellationToken ct) =>
            CommentEditContextDto.From(await query.GetAsync(await actors.GetRequiredAsync(ct), commentId, ct))).WithName("getCommentEditContext");
        app.MapPut("/api/v1/comments/{commentId}", async (Guid commentId, CommentUpdate body, HttpRequest request, CurrentActorAccessor actors, CommentService service, CancellationToken ct) =>
            CommentEditContextDto.From(await service.EditAsync(await actors.GetRequiredAsync(ct), commentId, () => ResourceVersion.Read(request), body.Content, ct))).WithName("updateComment");
        app.MapDelete("/api/v1/comments/{commentId}", async (Guid commentId, HttpRequest request, CurrentActorAccessor actors, CommentService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(await actors.GetRequiredAsync(ct), commentId, () => ResourceVersion.Read(request), ct);
            return Results.NoContent();
        }).WithName("deleteComment");
    }
}
