using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlogEngine.Api.Contracts.Discussions;
using BlogEngine.Api.Contracts.Reading;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Discussions.Application;
using BlogEngine.Core.Discussions.Contracts;
using BlogEngine.Core.Engagement.Application;
using BlogEngine.Core.Engagement.Contracts;
using BlogEngine.Core.Publishing.Application;
using BlogEngine.Core.Publishing.Contracts;
using BlogEngine.Core.ReadModels;
using BlogEngine.Infrastructure.Identity;
using BlogEngine.Infrastructure.Maintenance;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Accounts;
using BlogEngine.Infrastructure.Persistence.Discussions;
using BlogEngine.Infrastructure.Persistence.Engagement;
using BlogEngine.Infrastructure.Persistence.Publishing;
using BlogEngine.Infrastructure.ReadModels;
using BlogEngine.Infrastructure.Visitors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace BlogEngine.Api.Configuration;

public static class ApiRegistration
{
    public static void AddBlogEngine(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddOptions<IdentityOptions>().BindConfiguration("Identity").ValidateOnStart();
        services.AddSingleton<IValidateOptions<IdentityOptions>, IdentityOptionsValidator>();
        services.AddOptions<ApiOptions>().BindConfiguration("Api")
            .Configure(options => options.ConnectionString = builder.Configuration.GetConnectionString("Blog") ?? string.Empty).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();
        services.AddOptions<AnonymousVisitorOptions>().BindConfiguration("AnonymousVisitors").ValidateOnStart();
        services.AddSingleton<IValidateOptions<AnonymousVisitorOptions>, AnonymousVisitorOptionsValidator>();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AnonymousVisitorOptions>>().Value;
            return new AnonymousVisitorTokenService(options.ActiveKeyId, options.Keys);
        });
        services.AddSingleton<VisitorIdentityResolver>();
        services.AddSingleton<DatabaseClock>();
        services.AddDbContext<BlogDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<IOptions<ApiOptions>>().Value.ConnectionString,
            database => database.CommandTimeout(5)));
        services.AddScoped<IActorResolver, ActorResolver>();
        services.AddScoped<CurrentActorAccessor>();
        services.AddScoped<DependencyReadiness>();
        services.AddScoped<IPublicationWriteScope, PublicationWriteScope>();
        services.AddScoped<IPublicationReader, PublicationWriteScope>();
        services.AddScoped<IPublicationStore, PublicationStore>();
        services.AddScoped<ITaxonomyStore, TaxonomyStore>();
        services.AddScoped<PublicationService>();
        services.AddScoped<TaxonomyService>();
        services.AddScoped<WorkspaceQueries>();
        services.AddScoped<TaxonomyQueries>();
        services.AddScoped<PublicationDetailQuery>();
        services.AddScoped<IPublicFeedReader, PublicFeedQuery>();
        services.AddScoped<PublicationCountersQuery>();
        services.AddScoped<ICommentWriteStore, CommentWriteStore>();
        services.AddScoped<CommentService>();
        services.AddScoped<ICommentReader, CommentTreeQuery>();
        services.AddScoped<CommentEditQuery>();
        services.AddScoped<ModerationService>();
        services.AddScoped<ModerationQuery>();
        services.AddScoped<ILikeWriteStore, LikeWriteStore>();
        services.AddScoped<LikeService>();
        services.AddScoped<IViewWriteStore, ViewWriteStore>();
        services.AddScoped<ViewService>();
        services.AddScoped<ViewDeduplicationCleanup>();
        services.AddHostedService<ViewCleanupWorker>();
        services.AddSingleton(provider => new JwksSettings(new Uri(provider.GetRequiredService<IOptions<IdentityOptions>>().Value.JwksUrl)));
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton(provider => new JwksLoader(provider.GetRequiredService<HttpClient>(), provider.GetRequiredService<JwksSettings>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        services.AddSingleton<BearerKeyValidation>();
        services.AddBearerAuthentication();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
            options.SerializerOptions.RespectNullableAnnotations = true;
            options.SerializerOptions.AllowDuplicateProperties = false;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
        });
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options => options.Limits.MaxRequestBodySize = RequestContractValidation.MaximumBodyBytes);
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<IOptions<ApiOptions>>((cors, api) =>
            cors.AddDefaultPolicy(policy => policy.WithOrigins(api.Value.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));
        if (builder.Environment.IsDevelopment())
        {
            services.AddOpenApi(options =>
            {
                options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
                options.AddDocumentTransformer(CommentSchemas.TransformAsync);
                options.AddDocumentTransformer(ModerationOpenApi.TransformAsync);
                options.AddDocumentTransformer(LikeOpenApi.TransformAsync);
                options.AddDocumentTransformer(BlogEngine.Api.Contracts.Engagement.ViewOpenApi.TransformAsync);
            });
        }
        // Framework SQL/auth diagnostics can contain request data. The boundary writes safe structured events.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
        builder.Logging.AddFilter("Npgsql", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics", LogLevel.None);
    }

    public static async Task ValidateOptionalBearerAsync(HttpContext context, RequestDelegate next)
    {
        var optional = context.GetEndpoint()?.Metadata.GetMetadata<OptionalBearerContent>() is not null;
        var protectedEndpoint = context.GetEndpoint() is { } endpoint && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;
        context.Response.OnStarting(() =>
        {
            if (optional)
            {
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.Append("Vary", "Authorization");
            }
            else if (protectedEndpoint) { context.Response.Headers.CacheControl = "no-store"; }
            return Task.CompletedTask;
        });
        if (context.Request.Headers.ContainsKey("Authorization") && context.User.Identity?.IsAuthenticated != true)
        {
            await context.ChallengeAsync();
            return;
        }
        if (optional && context.User.Identity?.IsAuthenticated == true)
        {
            await context.RequestServices.GetRequiredService<CurrentActorAccessor>().GetRequiredAsync(context.RequestAborted);
        }
        await next(context);
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset().ToUniversalTime();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.UtcDateTime);
    }
}
