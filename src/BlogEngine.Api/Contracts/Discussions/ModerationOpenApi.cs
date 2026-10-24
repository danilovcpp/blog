using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BlogEngine.Api.Contracts.Discussions;

public static class ModerationOpenApi
{
    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes.TryAdd("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations?.Values.AsEnumerable() ?? []))
        {
            if (operation.OperationId is not ("listModerationComments" or "hideComment" or "restoreComment")) { continue; }
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
            operation.Description = "Moderator or Admin only. Requires Published parent. Request body must be absent. Hidden text is retained only in this private projection; Deleted text is always null.";
            operation.Parameters ??= [];
            if (operation.OperationId == "listModerationComments")
            {
                operation.Description += " Ordered by createdAt DESC, id DESC; no approval queue. A supplied absent/closed publicationId returns 404.";
                operation.Parameters.Add(Parameter("page", ParameterLocation.Query, new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), Default = JsonValue.Create(1) }));
                operation.Parameters.Add(Parameter("pageSize", ParameterLocation.Query, new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100", Default = JsonValue.Create(20) }));
                operation.Parameters.Add(Parameter("publicationId", ParameterLocation.Query, new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }));
                operation.Parameters.Add(Parameter("state", ParameterLocation.Query, new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("Visible"), JsonValue.Create("Hidden"), JsonValue.Create("Deleted")] }));
            }
            else
            {
                operation.Description += " Current-version desired state succeeds unchanged; stale version always returns 409. Deleted cannot be hidden or restored. Ancestor counts change atomically.";
                operation.Parameters.Add(Parameter("X-Resource-Version", ParameterLocation.Header, new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }, required: true));
            }
            foreach (var response in operation.Responses?.Values.OfType<OpenApiResponse>() ?? [])
            {
                response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                response.Headers["Cache-Control"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Const = "no-store" } };
            }
        }
        if (document.Components.Schemas?.TryGetValue(nameof(ModerationCommentDto), out var definition) == true && definition is OpenApiSchema schema)
        {
            schema.AdditionalPropertiesAllowed = false;
            schema.Required ??= new HashSet<string>(); schema.Required.UnionWith(schema.Properties!.Keys);
        }
        return Task.CompletedTask;
    }

    private static OpenApiParameter Parameter(string name, ParameterLocation location, OpenApiSchema schema, bool required = false) =>
        new() { Name = name, In = location, Schema = schema, Required = required };
}
