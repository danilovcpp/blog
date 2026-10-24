using System.Diagnostics;
using System.Text.Json;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Identity;
using BlogEngine.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace BlogEngine.Api.Diagnostics;

public sealed class ProblemDetailsHandler(RequestDelegate next, ILogger<ProblemDetailsHandler> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
            if (context.Response.StatusCode >= 400 && !context.Response.HasStarted && context.Response.ContentLength is null or 0)
            {
                await WriteAsync(context, context.Response.StatusCode, CodeFor(context.Response.StatusCode));
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted) { context.Response.StatusCode = 499; }
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var error = exception as DomainError ?? DatabaseErrorMapper.Map(exception);
            var status = error is not null ? StatusFor(error.Code) : exception switch
            {
                IdentityKeysUnavailableException => 503,
                BadHttpRequestException bad => bad.StatusCode,
                JsonException => 400,
                _ => 500,
            };
            var code = error?.Code ?? (exception is IdentityKeysUnavailableException ? "identity_keys_unavailable" : CodeFor(status));
            if (status == 500)
            {
                // Deliberately omit exception/message/stack: arbitrary exceptions can embed input or credentials.
                logger.LogError("Unhandled request failure {ExceptionType} at {Source}. TraceId {TraceId}",
                    exception.GetType().Name, exception.TargetSite?.DeclaringType?.FullName, TraceId(context));
            }
            context.Response.Clear();
            await WriteAsync(context, status, code, error?.Field);
        }
    }

    public static async Task WriteAsync(HttpContext context, int status, string code, string? field = null)
    {
        context.Response.StatusCode = status;
        if (status == 401) { context.Response.Headers.WWWAuthenticate = "Bearer"; }
        var problem = new ProblemDetails
        {
            Type = "about:blank",
            Title = TitleFor(status),
            Status = status,
            Extensions = { ["code"] = code, ["traceId"] = TraceId(context) },
        };
        if (field is not null) { problem.Extensions["errors"] = new Dictionary<string, string[]> { [field] = ["Invalid value."] }; }
        await context.Response.WriteAsJsonAsync(problem, options: (JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }

    public static string TraceId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;

    private static int StatusFor(string code) => code switch
    {
        "validation_failed" or "invalid_request" => 400,
        "authentication_required" or "invalid_token" => 401,
        "forbidden" => 403,
        "resource_not_found" => 404,
        "concurrency_conflict" or "slug_conflict" or "taxonomy_in_use" or "invalid_state" => 409,
        "request_too_large" => 413,
        "unsupported_media_type" => 415,
        "dependency_unavailable" or "identity_keys_unavailable" => 503,
        _ => 500,
    };

    private static string CodeFor(int status) => status switch
    {
        400 => "invalid_request",
        401 => "authentication_required",
        403 => "forbidden",
        404 => "resource_not_found",
        405 => "method_not_allowed",
        406 => "not_acceptable",
        409 => "concurrency_conflict",
        413 => "request_too_large",
        415 => "unsupported_media_type",
        503 => "dependency_unavailable",
        _ => "internal_error",
    };

    private static string TitleFor(int status) => status switch
    {
        400 => "Invalid request",
        401 => "Authentication required",
        403 => "Forbidden",
        404 => "Resource not found",
        405 => "Method not allowed",
        406 => "Not acceptable",
        409 => "Conflict",
        413 => "Request too large",
        415 => "Unsupported media type",
        503 => "Service unavailable",
        _ => "Internal server error",
    };
}
