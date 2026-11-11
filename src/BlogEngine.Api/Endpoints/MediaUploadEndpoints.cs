using BlogEngine.Api.Configuration;
using BlogEngine.Api.Contracts;
using BlogEngine.Api.Contracts.Media;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BlogEngine.Api.Endpoints;

public static class MediaUploadEndpoints
{
    public static void MapMediaUploadEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/media/uploads", async (UploadInitDto body, HttpRequest request, CurrentActorAccessor actors, UploadLifecycle service, CancellationToken ct) =>
        {
            var actor = await actors.GetRequiredAsync(ct);
            var values = request.Headers["Idempotency-Key"];
            if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out var key) || key == Guid.Empty)
            { throw DomainError.Validation("Idempotency-Key"); }
            if (!Enum.TryParse<MediaPurpose>(body.Purpose, out var purpose) || !Enum.IsDefined(purpose) || purpose.ToString() != body.Purpose)
            { throw DomainError.Validation("purpose"); }
            Guid? version = purpose == MediaPurpose.Avatar ? ResourceVersion.Read(request) : null;
            var upload = await service.InitializeAsync(actor, purpose, body.Name, body.DeclaredSize, body.DeclaredContentType, key, version, ct);
            return TypedResults.Created($"/api/v1/media/uploads/{upload.Id:D}", UploadDto.From(upload));
        }).WithName("createMediaUpload");

        app.MapGet("/api/v1/media/uploads/{id}", async (Guid id, CurrentActorAccessor actors, UploadLifecycle service, CancellationToken ct) =>
            UploadDto.From(await service.GetAsync(await actors.GetRequiredAsync(ct), id, ct))).WithName("getMediaUpload");

        app.MapDelete("/api/v1/media/uploads/{id}", async (Guid id, CurrentActorAccessor actors, UploadLifecycle service, CancellationToken ct) =>
        {
            await service.CancelAsync(await actors.GetRequiredAsync(ct), id, ct); return TypedResults.NoContent();
        }).WithName("cancelMediaUpload");

        app.MapPut("/api/v1/media/uploads/{id}/content", async (Guid id, HttpContext context, CurrentActorAccessor actors,
            UploadContentService service, IMediaSpool spool, CancellationToken ct) =>
        {
            await service.PutAsync(await actors.GetRequiredAsync(ct), id, async (purpose, token) =>
            {
                MediaBinaryTransport.PrepareUpload(context, purpose);
                try { return await spool.CaptureAsync(context.Request.Body, MediaRules.MaximumSize(purpose), token); }
                catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
                { throw new DomainError("request_too_large"); }
            }, ct);
            return TypedResults.NoContent();
        }).WithMetadata(new BinaryEndpoint("application/json", RawUpload: true)).WithName("putMediaUploadContent");

        app.MapPost("/api/v1/media/uploads/{id}/complete", async Task<Results<Ok<UploadResultDto>, Accepted<UploadDto>>>
            (Guid id, CurrentActorAccessor actors, UploadCompletion service, CancellationToken ct) =>
        {
            var upload = await service.RequestAsync(await actors.GetRequiredAsync(ct), id, ct);
            return upload.Result is { } result ? TypedResults.Ok(UploadResultDto.From(result)) : TypedResults.Accepted((string?)null, UploadDto.From(upload));
        }).WithName("completeMediaUpload");
    }
}
