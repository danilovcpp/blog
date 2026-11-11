using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace BlogEngine.Api.Configuration;

public sealed class MediaMaintenanceMiddleware(RequestDelegate next, IOptions<MediaOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (options.Value.MaintenanceMode && context.Request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
        { throw new DomainError("dependency_unavailable"); }
        return next(context);
    }
}
