using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BlogEngine.Api.Contracts.Reading;

public static class LikeOpenApi
{
    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes.TryAdd("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations?.Values.AsEnumerable() ?? []))
        {
            if (operation.OperationId is not ("setLike" or "removeLike")) { continue; }
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
            operation.Description = "Reader only. Published parent required. Sets only the current user's desired like state idempotently, atomically with counters. Request body must be absent; no resource version header. No public liker list.";
            foreach (var response in operation.Responses?.Values.OfType<OpenApiResponse>() ?? [])
            {
                response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                response.Headers["Cache-Control"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Const = "no-store" } };
            }
        }
        if (document.Components.Schemas?.TryGetValue(nameof(CountersDto), out var definition) == true && definition is OpenApiSchema schema)
        {
            schema.AdditionalPropertiesAllowed = false;
            schema.Required ??= new HashSet<string>(); schema.Required.UnionWith(schema.Properties!.Keys);
            foreach (var name in new[] { "likeCount", "viewCount" })
            { if (schema.Properties[name] is OpenApiSchema counter) { counter.Minimum = "0"; counter.Maximum = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture); } }
        }
        return Task.CompletedTask;
    }
}
