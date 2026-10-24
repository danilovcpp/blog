using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts;
using BlogEngine.Core.Accounts.Application;
using BlogEngine.Core.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BlogEngine.IntegrationTests.Fixtures;

// Test endpoints exercise the production middleware. No probes are compiled into the API.
public sealed class FoundationProbes : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseRouting();
        next(app);
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/__foundation/token", (HttpContext context) => Results.Ok(new { subject = context.User.FindFirst("sub")?.Value }))
                .RequireAuthorization();
            endpoints.MapGet("/__foundation/actor", async (CurrentActorAccessor accessor, CancellationToken token) =>
                Results.Ok(await accessor.GetRequiredAsync(token))).RequireAuthorization();
            endpoints.MapGet("/__foundation/author", async (CurrentActorAccessor accessor, CancellationToken token) =>
            {
                AccessPolicy.Demand(await accessor.GetRequiredAsync(token), ActorAction.CreatePublication);
                return Results.Ok();
            }).RequireAuthorization();
            endpoints.MapGet("/__foundation/public", () => Results.Ok(new { value = "public" }))
                .AllowAnonymous().WithMetadata(new OptionalBearerContent());
            endpoints.MapPost("/__foundation/json", (ProbeWrite body) => Results.Ok(body)).AllowAnonymous();
            endpoints.MapPost("/__foundation/markdown", (MarkdownWrite body) =>
            {
                TextRules.RequireMarkdown(body.Markdown);
                return Results.NoContent();
            }).AllowAnonymous();
            endpoints.MapPost("/__foundation/bodyless", () => Results.NoContent()).AllowAnonymous();
            endpoints.MapGet("/__foundation/id/{id}", (Guid id, int? page, int? pageSize) =>
                Results.Ok(new { id, offset = new PageRequest(page ?? 1, pageSize ?? 20).Offset })).AllowAnonymous();
            endpoints.MapPost("/__foundation/version", (HttpContext context) =>
            {
                var version = ResourceVersion.Read(context.Request);
                ResourceVersion.RequireCurrent(version, Guid.Parse("11111111-1111-1111-1111-111111111111"));
                return Results.NoContent();
            }).AllowAnonymous();
            endpoints.MapGet("/__foundation/error", () => ThrowFailure()).AllowAnonymous();
            endpoints.MapGet("/__foundation/cancel", () => ThrowCancellation()).AllowAnonymous();
            endpoints.MapGet("/__foundation/time", () => Results.Ok(new { now = DateTimeOffset.UnixEpoch })).AllowAnonymous();
            endpoints.MapGet("/__foundation/private", () => Results.Ok()); // fallback policy
        });
    };

    private static IResult ThrowFailure() => throw new InvalidOperationException("sensitive exception sentinel");
    private static IResult ThrowCancellation() => throw new OperationCanceledException("unrelated cancellation sentinel");
    public sealed record ProbeWrite(string Name);
    public sealed record MarkdownWrite(string Markdown);
}
