using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace BlogEngine.Api.Configuration;

// The reviewed consumer contract travels with the binary. Route identity is checked before
// applying descriptions, response unions and constraints that cannot be inferred from handlers.
public static class OpenApiContract
{
    public static async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        await using var stream = typeof(OpenApiContract).Assembly.GetManifestResourceStream("BlogEngine.Api.openapi.yaml")
            ?? throw new InvalidOperationException("The normative API contract is missing.");
        var settings = new OpenApiReaderSettings(); settings.AddYamlReader();
        var result = await OpenApiDocument.LoadAsync(stream, "yaml", settings, ct);
        if (result.Diagnostic?.Errors.Count > 0 || result.Document is null)
        { throw new InvalidOperationException("The normative API contract is invalid."); }
        var contract = result.Document;
        var runtime = document.Paths.Where(p => p.Key.StartsWith("/api/", StringComparison.Ordinal) || p.Key.StartsWith("/health/", StringComparison.Ordinal))
            .SelectMany(p => p.Value.Operations!.Select(o => (Path: p.Key, Method: o.Key, Operation: o.Value)))
            .ToDictionary(o => o.Operation.OperationId ?? throw new InvalidOperationException("An API operation has no stable name."));
        var count = 0;
        foreach (var (path, item) in contract.Paths)
        {
            foreach (var (method, operation) in item.Operations!)
            {
                if (!runtime.TryGetValue(operation.OperationId!, out var endpoint) || endpoint.Path != path || endpoint.Method != method)
                { throw new InvalidOperationException("API route identity differs from the normative contract: " + operation.OperationId); }
                // Inline shared error responses for consumers which do not expand response references.
                foreach (var (status, response) in operation.Responses!.ToArray())
                { if (response is OpenApiResponseReference reference) { operation.Responses![status] = reference.Target!; } }
                if (operation.Parameters is { } parameters)
                {
                    for (var i = 0; i < parameters.Count; i++)
                    { if (parameters[i] is OpenApiParameterReference reference) { parameters[i] = reference.Target!; } }
                }
                count++;
            }
        }
        if (count != runtime.Count) { throw new InvalidOperationException("An API route is absent from the normative contract."); }
        document.Info = contract.Info;
        document.Paths = contract.Paths;
        document.Servers = contract.Servers;
        document.Security = contract.Security;
        document.Components = contract.Components;
    }
}
