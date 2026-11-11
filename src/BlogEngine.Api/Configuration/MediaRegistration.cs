using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Media.Application;
using BlogEngine.Core.Media.Contracts;
using BlogEngine.Infrastructure.Maintenance;
using BlogEngine.Infrastructure.Media;
using BlogEngine.Infrastructure.Persistence.Accounts;
using BlogEngine.Infrastructure.Persistence.Media;
using BlogEngine.Infrastructure.ReadModels;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace BlogEngine.Api.Configuration;

public static class MediaRegistration
{
    public static void AddMedia(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<MediaOptions>().BindConfiguration("Media")
            .Validate(o => builder.Environment.IsDevelopment() || !o.Storage.AllowLocalHttp,
                "Media local HTTP opt-in is allowed only in Development.").ValidateOnStart();
        services.AddSingleton<IValidateOptions<MediaOptions>, MediaOptionsValidator>();
        services.AddSingleton<MediaSpool>();
        services.AddSingleton<IMediaSpool>(p => p.GetRequiredService<MediaSpool>());
        services.AddSingleton<IMediaObjectStorage, GarageObjectStorage>();
        services.AddSingleton<IMediaImageCodec, SkiaImageCodec>();
        services.AddHostedService<MediaSpoolLifetime>();
        services.AddScoped<IAccountRoleWriteGuard, AccountRoleWriteGuard>();
        services.AddScoped<IMediaWriteStore, MediaWriteStore>();
        services.AddScoped<MediaBinaryTransport>();
        services.AddScoped<IUploadLifecycleStore, UploadLifecycleStore>();
        services.AddScoped<IUploadTransferStore, UploadTransferStore>();
        services.AddScoped<IMediaProcessingStore, MediaProcessingStore>();
        services.AddSingleton<IMediaProcessingLease, MediaProcessingLease>();
        services.AddScoped<IMediaUploadProcessor, AttachmentProcessing>();
        services.AddScoped<IMediaUploadProcessor, PublicationImageProcessing>();
        services.AddSingleton<IPublicationImageRenderer, PublicationImageRenderer>();
        services.AddScoped<IPublicationImageStore, MediaProcessingStore>();
        services.AddScoped<IMediaUploadProcessor, AvatarProcessing>();
        services.AddSingleton<IAvatarRenderer, AvatarRenderer>();
        services.AddScoped<IAvatarCompletionStore, AvatarCompletionStore>();
        services.AddScoped<IAvatarStateStore, AvatarStateStore>();
        services.AddScoped<AvatarService>();
        services.AddScoped<AvatarContentQuery>();
        services.AddScoped<IAuthorAvatarReader, AuthorAvatarQuery>();
        services.AddScoped<IPublicationMediaUsages, PublicationMediaUsages>();
        services.AddScoped<IFileDeletionStore, FileDeletionStore>();
        services.AddScoped<FileDeletion>();
        services.AddScoped<PublicationImageQuery>();
        services.AddScoped<PublicationMediaAccessQuery>();
        services.AddScoped<IMediaLibraryQuery, MediaLibraryQuery>();
        services.AddScoped<UploadLifecycle>();
        services.AddScoped<UploadContentService>();
        services.AddScoped<UploadCompletion>();
        services.AddScoped<MediaLibrary>();
        services.AddHostedService<MediaProcessingWorker>();
    }

    private sealed class MediaSpoolLifetime(MediaSpool spool, IWebHostEnvironment environment, IOptions<MediaOptions> options) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Spool.Directory));
            var webroot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot")));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (path.Equals(webroot, comparison) || path.StartsWith(webroot + Path.DirectorySeparatorChar, comparison))
            { throw new OptionsValidationException("Media", typeof(MediaOptions), ["Media spool must be outside webroot."]); }
            // Native codec availability is a startup gate, including in the target Linux image.
            using var nativeProbe = new SKBitmap(1, 1);
            return spool.StartAsync(ct);
        }
        public Task StopAsync(CancellationToken ct) => spool.StopAsync(ct);
    }
}
