using System.Diagnostics;
using BlogEngine.Infrastructure.Media;

namespace BlogEngine.Api.Diagnostics;

public sealed class RequestLogging(RequestDelegate next, ILogger<RequestLogging> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/media") || context.Request.Path.StartsWithSegments("/api/v1/avatars"))
            { MediaTelemetry.RequestDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("status", context.Response.StatusCode)); }
            logger.LogInformation("HTTP {Method} completed {StatusCode} in {ElapsedMs} ms. TraceId {TraceId}",
                context.Request.Method, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ProblemDetailsHandler.TraceId(context));
        }
    }
}
