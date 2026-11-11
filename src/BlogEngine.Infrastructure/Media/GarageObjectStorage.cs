using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Shared;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Media;

public sealed class GarageObjectStorage : IMediaObjectStorage, IDisposable
{
    private readonly AmazonS3Client client;
    private readonly MediaStorageOptions settings;
    private readonly IMediaSpool spool;
    public GarageObjectStorage(IOptions<MediaOptions> options, IMediaSpool spool)
    {
        settings = options.Value.Storage; this.spool = spool;
        client = new(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey), new AmazonS3Config
        {
            ServiceURL = settings.Endpoint,
            AuthenticationRegion = "garage",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = settings.RequestTimeout,
            MaxErrorRetry = 0,
        });
    }

    public async Task PutAsync(MediaBucketKind bucket, string key, IMediaSpoolFile content, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        await using var source = content.OpenRead();
        try
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket(bucket),
                Key = key,
                InputStream = source,
                ContentType = "application/octet-stream",
                UseChunkEncoding = false,
                DisablePayloadSigning = false,
                MD5Digest = Convert.ToBase64String(content.Md5.Span),
                AutoCloseStream = false,
                AutoResetStreamPosition = false,
                Headers = { ContentLength = content.Length },
            }, deadline.Token);
        }
        catch (Exception e) when (StorageFailure(e, ct)) { throw Classify(e); }
    }

    public async Task<IMediaSpoolFile> GetVerifiedAsync(MediaBucketKind bucket, string key, long length, ReadOnlyMemory<byte> sha256, CancellationToken ct)
    {
        if (length <= 0 || sha256.Length != 32) { throw new ArgumentException("Verified length and SHA-256 are required."); }
        using var deadline = Deadline(ct);
        try
        {
            using var response = await client.GetObjectAsync(Bucket(bucket), key, deadline.Token);
            if (response.ContentLength != length) { throw IntegrityFailure(); }
            var file = await spool.CaptureAsync(response.ResponseStream, length, deadline.Token);
            if (file.Length != length || !CryptographicOperations.FixedTimeEquals(file.Sha256.Span, sha256.Span))
            { await file.DisposeAsync(); throw IntegrityFailure(); }
            return file;
        }
        catch (DomainError e) when (e.Code == "request_too_large") { throw IntegrityFailure(); }
        catch (Exception e) when (StorageFailure(e, ct)) { throw Classify(e); }
    }

    public async Task DeleteAsync(MediaBucketKind bucket, string key, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        try { await client.DeleteObjectAsync(Bucket(bucket), key, deadline.Token); }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode == "NoSuchKey") { }
        catch (Exception e) when (StorageFailure(e, ct)) { throw Classify(e); }
    }

    private CancellationTokenSource Deadline(CancellationToken ct)
    { var source = CancellationTokenSource.CreateLinkedTokenSource(ct); source.CancelAfter(settings.RequestTimeout); return source; }
    private string Bucket(MediaBucketKind bucket) => bucket switch
    { MediaBucketKind.Originals => settings.OriginalsBucket, MediaBucketKind.Renditions => settings.RenditionsBucket, _ => throw new ArgumentOutOfRangeException(nameof(bucket)) };
    private static bool StorageFailure(Exception e, CancellationToken caller) => e is AmazonS3Exception or HttpRequestException or IOException || (e is OperationCanceledException && !caller.IsCancellationRequested);
    private static MediaStorageException IntegrityFailure()
    {
        MediaTelemetry.StorageFailures.Add(1, new KeyValuePair<string, object?>("code", "media_integrity_failure"));
        return new("media_integrity_failure", false);
    }
    private static MediaStorageException Classify(Exception e)
    {
        var code = e is AmazonS3Exception { StatusCode: HttpStatusCode.NotFound, ErrorCode: "NoSuchKey" } ? "media_object_missing" : "media_storage_unavailable";
        MediaTelemetry.StorageFailures.Add(1, new KeyValuePair<string, object?>("code", code));
        return e is AmazonS3Exception s3
        ? new(code,
            (int)s3.StatusCode >= 500 || s3.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || s3.StatusCode == 0)
        : new("media_storage_unavailable", true);
    }
    public void Dispose() => client.Dispose();
}
