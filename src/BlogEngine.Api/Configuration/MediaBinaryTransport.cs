using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace BlogEngine.Api.Configuration;

public sealed record BinaryEndpoint(string ContentType, bool RawUpload = false);
public sealed record MediaDownloadAccess(MediaBucketKind Bucket, string Key, long Length, ReadOnlyMemory<byte> Sha256,
    string ContentType, string? FileName = null);

public sealed class MediaBinaryTransport(IMediaObjectStorage storage, IMediaSpool spool)
{
    // Invoke only after the upload owner/state/role claim has succeeded, before any body read.
    // In particular, 413 from Kestrel remains visible to the fenced upload scenario.
    public static void PrepareUpload(HttpContext context, MediaPurpose purpose)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature?.IsReadOnly == true) { throw new InvalidOperationException("Upload body was read before authorization."); }
        if (feature is not null) { feature.MaxRequestBodySize = MediaRules.MaximumSize(purpose); }
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) ||
            !type.MediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) || type.Parameters.Count != 0)
        { throw new DomainError("unsupported_media_type"); }
    }

    // The callback reads current local roles and the resource together on every call, without cached actor roles.
    public async Task DownloadAsync(HttpContext context, Func<CancellationToken, Task<MediaDownloadAccess?>> authorize)
    {
        var ct = context.RequestAborted;
        var access = await authorize(ct) ?? throw new DomainError("resource_not_found");
        IMediaTransfer? transfer = null;
        IMediaSpoolFile? file = null;
        MediaStorageException? failure = null;
        try
        {
            try
            {
                transfer = await spool.AcquireTransferAsync(ct);
                file = await storage.GetVerifiedAsync(access.Bucket, access.Key, access.Length, access.Sha256, transfer.CancellationToken);
            }
            catch (MediaStorageException error) { failure = error; }
            catch (DomainError error) when (error.Code == "media_capacity_unavailable") { failure = new("media_capacity_unavailable", true); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = new("media_storage_unavailable", true); }
            var current = await authorize(ct) ?? throw new DomainError("resource_not_found");
            if (current.Bucket != access.Bucket || current.Key != access.Key || current.Length != access.Length || !current.Sha256.Span.SequenceEqual(access.Sha256.Span))
            { throw new DomainError("resource_not_found"); }
            if (failure is not null) { throw new DomainError("media_storage_unavailable"); }
            context.Response.ContentType = current.ContentType;
            context.Response.ContentLength = file!.Length;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            var disposition = new ContentDispositionHeaderValue(current.FileName is null ? "inline" : "attachment");
            if (current.FileName is not null) { disposition.SetHttpFileName(MediaRules.FileName(current.FileName)); }
            context.Response.GetTypedHeaders().ContentDisposition = disposition;
            await using var source = file.OpenRead();
            await source.CopyToAsync(context.Response.Body, 64 * 1024, transfer!.CancellationToken);
        }
        finally
        {
            if (file is not null) { await file.DisposeAsync(); }
            if (transfer is not null) { await transfer.DisposeAsync(); }
        }
    }
}
