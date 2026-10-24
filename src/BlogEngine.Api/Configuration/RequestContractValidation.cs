using BlogEngine.Api.Diagnostics;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Net.Http.Headers;

namespace BlogEngine.Api.Configuration;

public sealed class RequestContractValidation(RequestDelegate next)
{
    public const long MaximumBodyBytes = 8 * 1024 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is not null)
        {
            IList<MediaTypeHeaderValue>? accept;
            try { accept = context.Request.GetTypedHeaders().Accept; }
            catch (FormatException) { throw new BadHttpRequestException("Invalid Accept header."); }
            if (accept is { Count: > 0 } && !accept.Any(value => value.Quality.GetValueOrDefault(1) > 0 &&
                (new MediaTypeHeaderValue("application/json").IsSubsetOf(value) || new MediaTypeHeaderValue("application/problem+json").IsSubsetOf(value))))
            {
                await ProblemDetailsHandler.WriteAsync(context, 406, "not_acceptable");
                return;
            }
            if (context.Request.ContentLength > MaximumBodyBytes)
            {
                await ProblemDetailsHandler.WriteAsync(context, 413, "request_too_large");
                return;
            }
            var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
            if (accepts?.RequestType is null)
            {
                // Inspect one byte, including chunked bodies; Content-Length alone is insufficient.
                var one = new byte[1];
                if (await context.Request.Body.ReadAsync(one, context.RequestAborted) != 0)
                {
                    await ProblemDetailsHandler.WriteAsync(context, 400, "invalid_request");
                    return;
                }
            }
            else
            {
                // Bounded buffering also covers TestServer and chunked bodies without a Content-Length.
                var original = context.Request.Body;
                await using var buffered = new MemoryStream();
                var bytes = new byte[8192];
                int read;
                while ((read = await original.ReadAsync(bytes, context.RequestAborted)) != 0)
                {
                    if (buffered.Length + read > MaximumBodyBytes)
                    {
                        await ProblemDetailsHandler.WriteAsync(context, 413, "request_too_large");
                        return;
                    }
                    await buffered.WriteAsync(bytes.AsMemory(0, read), context.RequestAborted);
                }
                buffered.Position = 0;
                context.Request.Body = buffered;
                try { await next(context); }
                finally { context.Request.Body = original; }
                return;
            }
        }
        await next(context);
    }
}
