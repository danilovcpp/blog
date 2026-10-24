using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BlogEngine.Api.Contracts.Discussions;

public static class CommentSchemas
{
    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        if (document.Components?.Schemas is not { } schemas) { return Task.CompletedTask; }
        foreach (var (name, definition) in schemas)
        {
            if (definition is not OpenApiSchema schema) { continue; }
            if (name == nameof(PublicCommentDto))
            {
                // Serialization chooses a derived shape without emitting a synthetic $type property.
                schema.Discriminator = null; schema.OneOf = schema.AnyOf; schema.AnyOf = null;
            }
            var visible = name.EndsWith(nameof(VisibleCommentDto), StringComparison.Ordinal);
            var placeholder = name.EndsWith(nameof(CommentPlaceholderDto), StringComparison.Ordinal);
            if (!visible && !placeholder && name != nameof(CommentEditContextDto)) { continue; }
            schema.Type = JsonSchemaType.Object;
            schema.Required ??= new HashSet<string>(); schema.Required.UnionWith(["state", "content", "author"]);
            schema.Properties!["createdAt"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" };
            schema.Properties["editedAt"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, Format = "date-time" };
            schema.Properties["depth"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "5" };
            if (placeholder)
            {
                schema.Properties["content"] = new OpenApiSchema { Type = JsonSchemaType.Null };
                schema.Properties["author"] = new OpenApiSchema { Type = JsonSchemaType.Null };
                schema.Properties["state"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("Hidden"), JsonValue.Create("Deleted")] };
            }
            else
            {
                schema.Properties["content"] = new OpenApiSchema
                {
                    Type = visible ? JsonSchemaType.String : JsonSchemaType.String | JsonSchemaType.Null,
                    MinLength = 1,
                    MaxLength = 5000,
                    Description = "Trimmed plain untrusted text; length in Unicode scalar values.",
                };
                if (visible)
                { schema.Properties["state"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("Visible")] }; }
                else
                {
                    schema.Description = "Private owner/Admin context for a Published material. Owner Hidden content is null; Admin sees retained Hidden text. Deleted content is always null.";
                    schema.OneOf = [PrivateState("Visible", JsonSchemaType.String), PrivateState("Hidden", JsonSchemaType.String | JsonSchemaType.Null), PrivateState("Deleted", JsonSchemaType.Null)];
                }
            }
        }
        return Task.CompletedTask;
    }

    private static OpenApiSchema PrivateState(string state, JsonSchemaType contentType) => new()
    {
        Type = JsonSchemaType.Object,
        Required = new HashSet<string>(["state", "content"]),
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["state"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(state)] },
            ["content"] = new OpenApiSchema { Type = contentType },
        },
    };
}
