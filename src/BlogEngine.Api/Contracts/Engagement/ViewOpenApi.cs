using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BlogEngine.Api.Contracts.Engagement;

public static class ViewOpenApi
{
    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations?.Values.AsEnumerable() ?? []))
        {
            if (operation.OperationId != "registerView") { continue; }
            operation.Security = [new OpenApiSecurityRequirement(), new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
            operation.Description = "Bodyless explicit opening of a Published material. Fixed 24-hour window; equality counts. Verified Bearer takes priority across devices, without merging anonymous history. Store and repeat the issued anonymous token. No token on failure.";
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "X-Anonymous-Visitor",
                In = ParameterLocation.Header,
                Required = false,
                Description = "Opaque engine-issued token. Missing/invalid/over 256 characters is replaced on success; ignored with verified Bearer.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 256 },
            });
            foreach (var response in operation.Responses?.Values.OfType<OpenApiResponse>() ?? [])
            {
                response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                response.Headers["Cache-Control"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Const = "private, no-store" } };
            }
        }
        if (document.Components?.Schemas?.TryGetValue(nameof(ViewResultDto), out var definition) == true && definition is OpenApiSchema schema)
        {
            schema.AdditionalPropertiesAllowed = false; schema.Required = new HashSet<string>(schema.Properties!.Keys);
            if (schema.Properties["viewCount"] is OpenApiSchema count) { count.Minimum = "0"; }
            if (schema.Properties["anonymousVisitorId"] is OpenApiSchema visitor) { visitor.MaxLength = 256; }
        }
        return Task.CompletedTask;
    }
}
