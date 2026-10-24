using System.Diagnostics;

namespace BlogEngine.Api.Diagnostics;

public sealed class RequestLogging(RequestDelegate next, ILogger<RequestLogging> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            logger.LogInformation("HTTP {Method} completed {StatusCode} in {ElapsedMs} ms. TraceId {TraceId}",
                context.Request.Method, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ProblemDetailsHandler.TraceId(context));
        }
    }
}
