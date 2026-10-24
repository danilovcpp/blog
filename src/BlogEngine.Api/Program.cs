using BlogEngine.Api.Configuration;
using BlogEngine.Api.Diagnostics;
using BlogEngine.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.AddBlogEngine();
var app = builder.Build();

app.UseMiddleware<RequestLogging>();
app.UseMiddleware<ProblemDetailsHandler>();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Blog Engine v1"));
}
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.Use(ApiRegistration.ValidateOptionalBearerAsync);
// Routing's missing/method-rejection endpoints have no application metadata. Return protocol errors before the fallback policy.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint() is null) { await ProblemDetailsHandler.WriteAsync(context, 404, "resource_not_found"); return; }
    if (context.GetEndpoint()!.DisplayName is "405 HTTP Method Not Supported" or "415 HTTP Unsupported Media Type")
    {
        await context.GetEndpoint()!.RequestDelegate!(context); // the method matcher sets Allow and 405
        return;
    }
    await next(context);
});
app.UseAuthorization();
app.UseMiddleware<RequestContractValidation>();
app.MapHealthEndpoints();
app.MapMeEndpoints();
app.MapPublicationCommandEndpoints();
app.MapWorkspaceEndpoints();
app.MapTaxonomyEndpoints();
app.MapPublicationReadEndpoints();
app.MapFeedEndpoints();
app.MapCounterEndpoints();
app.MapCommentEndpoints();
app.MapModerationEndpoints();
app.MapLikeEndpoints();
app.MapViewEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

await app.RunAsync();

// WebApplicationFactory uses this entry point; test probes are registered only by its test host.
public partial class Program;
