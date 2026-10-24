using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts;
using BlogEngine.Api.Contracts.Publishing;
using BlogEngine.Core.Publishing.Application;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.ReadModels;
using BlogEngine.Infrastructure.ReadModels;

namespace BlogEngine.Api.Endpoints;

public static class TaxonomyEndpoints
{
    public static void MapTaxonomyEndpoints(this WebApplication app)
    {
        Map(app, "categories", "Category", "Categories", TaxonomyKind.Category);
        Map(app, "tags", "Tag", "Tags", TaxonomyKind.Tag);
    }
    private static void Map(WebApplication app, string path, string singular, string plural, TaxonomyKind kind)
    {
        var route = "/api/v1/" + path;
        app.MapGet(route, async (HttpRequest request, TaxonomyQueries queries, CancellationToken ct) =>
        {
            var page = await queries.ListAsync(kind, PublishingValidation.Page(request), ct);
            return new PageResult<TaxonomyDto>(page.Items.Select(TaxonomyDto.From).ToArray(), page.Page, page.PageSize, page.HasNextPage);
        }).AllowAnonymous().WithMetadata(new OptionalBearerContent()).WithName("list" + plural);
        app.MapGet(route + "/{id}", async (Guid id, TaxonomyQueries queries, CancellationToken ct) =>
            TaxonomyDto.From(await queries.GetAsync(kind, id, ct))).AllowAnonymous().WithMetadata(new OptionalBearerContent()).WithName("get" + singular);
        app.MapPost(route, async (TaxonomyCreate body, CurrentActorAccessor actors, TaxonomyService service, CancellationToken ct) =>
        {
            var item = await service.CreateAsync(await actors.GetRequiredAsync(ct), kind, body.Name, body.Slug, ct);
            return Results.Created(route + "/" + item.Id.ToString("D"), TaxonomyDto.From(item));
        }).WithName("create" + singular);
        app.MapPut(route + "/{id}", async (Guid id, TaxonomyRename body, HttpRequest request, CurrentActorAccessor actors, TaxonomyService service, CancellationToken ct) =>
            TaxonomyDto.From((await service.MutateAsync(await actors.GetRequiredAsync(ct), kind, id, () => ResourceVersion.Read(request), body.Name, ct))!)).WithName("rename" + singular);
        app.MapDelete(route + "/{id}", async (Guid id, HttpRequest request, CurrentActorAccessor actors, TaxonomyService service, CancellationToken ct) =>
        {
            await service.MutateAsync(await actors.GetRequiredAsync(ct), kind, id, () => ResourceVersion.Read(request), null, ct);
            return Results.NoContent();
        }).WithName("delete" + singular);
    }
}
