using System.Net;
using System.Text.Json.Nodes;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Contracts;

[Trait("Category", "ConsumerContract")]
public sealed class OpenApiCompatibilityTests
{
    [Fact]
    public async Task NormativeSchemasRejectHiddenNullTextMalformedIdsAndSystemFieldLeaks()
    {
        var contract = await ContractAsync(TestContext.Current.CancellationToken);
        var schema = contract["components"]!["schemas"]!["ModerationComment"]!;
        var comment = JsonNode.Parse("""
            {"id":"11111111-1111-1111-1111-111111111111","publicationId":"11111111-1111-1111-1111-111111111111","parentId":null,"depth":1,"state":"Hidden","content":"retained","author":{"id":"11111111-1111-1111-1111-111111111111"},"createdAt":"2026-10-01T00:00:00Z","editedAt":null,"version":"11111111-1111-1111-1111-111111111111"}
            """)!;
        ConsumerWalkthroughTests.Validate(contract, schema, comment, "moderation");
        foreach (var (field, value) in new (string, JsonNode?)[] { ("content", null), ("id", JsonValue.Create("bad")), ("depth", JsonValue.Create(6)), ("secret", JsonValue.Create("unexpected")) })
        {
            var invalid = comment.DeepClone(); invalid[field] = value;
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ConsumerWalkthroughTests.Validate(contract, schema, invalid, "moderation"));
        }
        foreach (var (name, definition) in contract["components"]!["schemas"]!.AsObject())
        { if (definition?["properties"]?["anonymousVisitorId"] is not null) { Assert.Equal("ViewResult", name); } }
    }

    internal static async Task<JsonObject> ContractAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetDirectoryName(ArtifactPath("unused"))!, "..", "..", "specs", "001-blog-engine-mvp", "contracts", "openapi.yaml");
        var settings = new OpenApiReaderSettings(); settings.AddYamlReader();
        var result = await OpenApiDocument.LoadAsync(path, settings, ct);
        Assert.Empty(result.Diagnostic!.Errors);
        return JsonNode.Parse(await result.Document!.SerializeAsync(OpenApiSpecVersion.OpenApi3_1, "json", cancellationToken: ct))!.AsObject();
    }

    [Fact]
    public async Task EveryNormativeOperationAndSchemaIsPublishedWithoutContractDrift()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var identity = new JwksFixture(); await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        var expected = await ContractAsync(ct);
        var actual = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json", ct))!.AsObject();
        var comparisons = new List<object>();
        foreach (var (path, item) in expected["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (method is not ("get" or "post" or "put" or "delete")) { continue; }
                var generated = actual["paths"]?[path]?[method]; Assert.NotNull(generated);
                foreach (var field in new[] { "operationId", "parameters", "requestBody", "responses", "security" })
                { Assert.True(JsonNode.DeepEquals(Expand(expected, operation?[field]), Expand(actual, generated[field])), $"{method} {path}: {field}"); }
                comparisons.Add(new { path, method, operationId = operation!["operationId"]!.GetValue<string>(), passed = true });
            }
        }
        foreach (var (name, schema) in expected["components"]!["schemas"]!.AsObject())
        { Assert.True(JsonNode.DeepEquals(schema, actual["components"]!["schemas"]![name]), "Schema " + name); }
        var publicOperations = actual["paths"]!.AsObject().Where(p => p.Key.StartsWith("/api/", StringComparison.Ordinal) || p.Key.StartsWith("/health/", StringComparison.Ordinal))
            .SelectMany(p => p.Value!.AsObject().Where(o => o.Key is "get" or "post" or "put" or "delete")).ToArray();
        Assert.Equal(comparisons.Count, publicOperations.Length);
        await File.WriteAllTextAsync(ArtifactPath("contracts.json"), new JsonObject
        {
            ["status"] = "passed",
            ["operations"] = System.Text.Json.JsonSerializer.SerializeToNode(comparisons),
            ["schemas"] = expected["components"]!["schemas"]!.AsObject().Count,
        }.ToJsonString(), ct);
    }

    private static JsonNode? Expand(JsonNode document, JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is { } reference)
            {
                JsonNode? target = document;
                foreach (var segment in reference.GetValue<string>().Split('/').Skip(1)) { target = target![segment]; }
                return Expand(document, target);
            }
            return new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, Expand(document, pair.Value))));
        }
        return node is JsonArray array ? new JsonArray(array.Select(item => Expand(document, item)).ToArray()) : node?.DeepClone();
    }
}
