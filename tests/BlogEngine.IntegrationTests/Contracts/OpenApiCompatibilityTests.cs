using System.Text.Json.Nodes;
using BlogEngine.Api.Configuration;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Contracts;

[Trait("Category", "ConsumerContract")]
public sealed class OpenApiCompatibilityTests
{
    private static readonly HashSet<string> ChangedMvpOperations =
    [
        "getMe", "listPublications", "getPublication", "getPublicationBySlug",
        "listWorkspacePublications", "getWorkspacePublication", "createPublication", "updatePublication",
        "publishPublication", "unpublishPublication", "listComments", "createComment",
        "getCommentEditContext", "updateComment", "listModerationComments", "hideComment", "restoreComment",
    ];

    [Fact]
    public async Task GeneratedBindingAndSuccessSchemasMatchBeforeDocumentOverlay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity)
        {
            CustomizeServices = services => services.AddOpenApi("binding", options =>
            {
                options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
                options.AddOperationTransformer(OpenApiContract.DescribeBinaryAsync);
            }),
        };
        using var client = factory.CreateClient();
        var generated = JsonNode.Parse(await client.GetStringAsync("/openapi/binding.json", ct))!;
        await File.WriteAllTextAsync(ArtifactPath("media/pre-overlay.json"), generated.ToJsonString(), ct);
        var contract = await ContractAsync(ct);
        AssertBinding(contract, generated);

        var request = generated["paths"]!["/api/v1/media/uploads"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!;
        var properties = Resolve(generated, request)["properties"]!.AsObject();
        Assert.True(properties.Remove("declaredSize"));
        var drift = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertBinding(contract, generated));
        Assert.Contains("createMediaUpload request", drift.Message, StringComparison.Ordinal);
    }

    private static void AssertBinding(JsonNode contract, JsonNode generated)
    {
        foreach (var (path, item) in contract["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject().Where(pair => IsMethod(pair.Key)))
            {
                var name = operation!["operationId"]!.GetValue<string>();
                var actual = generated["paths"]?[path]?[method];
                Assert.NotNull(actual);
                Assert.Equal(name, actual["operationId"]!.GetValue<string>());
                if (!ChangedMvpOperations.Contains(name) && !IsMedia(name)) { continue; }
                Assert.Equal(PathParameters(contract, item, operation),
                    PathParameters(generated, generated["paths"]![path], actual));
                var expectedBody = operation["requestBody"];
                var actualBody = actual["requestBody"];
                Assert.Equal(expectedBody is null, actualBody is null);
                if (expectedBody is not null)
                {
                    Assert.Equal(expectedBody["required"]!.GetValue<bool>(), actualBody!["required"]!.GetValue<bool>());
                    AssertContent(contract, generated, expectedBody["content"], actualBody["content"], name + " request");
                }
                foreach (var (status, response) in operation["responses"]!.AsObject().Where(pair => pair.Key.StartsWith('2')))
                {
                    var runtimeResponse = actual["responses"]?[status];
                    Assert.True(runtimeResponse is not null, name + " status " + status);
                    AssertContent(contract, generated, Resolve(contract, response!)["content"], runtimeResponse!["content"], name + " " + status);
                }
            }
        }
    }

    private static IEnumerable<string> PathParameters(JsonNode document, JsonNode? item, JsonNode operation) =>
        (item?["parameters"]?.AsArray() ?? []).Concat(operation["parameters"]?.AsArray() ?? [])
            .Select(parameter => Resolve(document, parameter!))
            .Where(parameter => parameter["in"]?.ToString() == "path")
            .Select(parameter => parameter["name"]!.ToString())
            .Order();

    private static void AssertContent(JsonNode contract, JsonNode generated, JsonNode? expected, JsonNode? actual, string label)
    {
        Assert.Equal(expected?.AsObject().Select(p => p.Key).Order(), actual?.AsObject().Select(p => p.Key).Order());
        foreach (var (type, definition) in expected?.AsObject() ?? [])
        {
            if (definition?["schema"] is { } schema)
            {
                AssertShape(contract, generated, schema, actual![type]!["schema"]!, label);
            }
        }
    }

    private static void AssertShape(JsonNode contract, JsonNode generated, JsonNode expected, JsonNode actual, string label)
    {
        expected = Resolve(contract, expected);
        actual = Resolve(generated, actual);
        if ((actual["oneOf"] ?? actual["anyOf"]) is JsonArray alternatives)
        {
            foreach (var alternative in alternatives.Where(branch => branch?["type"]?.ToString() != "null"))
            {
                AssertShape(contract, generated, expected, alternative!, label);
            }
            return;
        }
        // Generated schemas describe CLR shapes. The HTTP walkthrough checks conditional unions,
        // wire nullability and domain constraints which the CLR types alone cannot express.
        if (expected["properties"] is null && (expected["oneOf"] ?? expected["anyOf"]) is JsonArray branches)
        {
            foreach (var branch in branches.Where(branch => branch?["type"]?.ToString() != "null"))
            {
                AssertShape(contract, generated, branch!, actual, label);
            }
            return;
        }
        if (expected["properties"] is JsonObject properties)
        {
            Assert.True(actual["properties"] is JsonObject actualProperties &&
                properties.Select(p => p.Key).Order().SequenceEqual(actualProperties.Select(p => p.Key).Order()),
                label + " schema: " + actual);
            foreach (var (name, property) in properties)
            {
                AssertShape(contract, generated, property!, actual["properties"]![name]!, label + "." + name);
            }
        }
        if (expected["items"] is { } items) { AssertShape(contract, generated, items, actual["items"]!, label + "[]"); }
        if (expected["format"]?.ToString() is "uuid" or "date-time" or "binary")
        {
            Assert.Equal(expected["format"]!.ToString(), actual["format"]?.ToString());
        }
    }

    internal static JsonNode Resolve(JsonNode contract, JsonNode node)
    {
        if (node["$ref"] is not { } reference) { return node; }
        var pointer = reference.GetValue<string>();
        Assert.StartsWith("#/", pointer, StringComparison.Ordinal);
        return pointer.Split('/').Skip(1).Aggregate(contract, (target, segment) =>
            target[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)]!);
    }

    internal static bool IsMethod(string name) => name is "get" or "post" or "put" or "delete";

    private static bool IsMedia(string operation) =>
        operation.Contains("Media", StringComparison.Ordinal) || operation.Contains("Avatar", StringComparison.Ordinal);

    [Fact]
    public async Task NormativeSchemasRejectHiddenNullTextMalformedIdsAndSystemFieldLeaks()
    {
        var contract = await ContractAsync(TestContext.Current.CancellationToken);
        var schema = contract["components"]!["schemas"]!["ModerationComment"]!;
        var comment = JsonNode.Parse("""
            {"id":"11111111-1111-1111-1111-111111111111","publicationId":"11111111-1111-1111-1111-111111111111","parentId":null,"depth":1,"state":"Hidden","content":"retained","author":{"id":"11111111-1111-1111-1111-111111111111","avatar":null},"createdAt":"2026-10-01T00:00:00Z","editedAt":null,"version":"11111111-1111-1111-1111-111111111111"}
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
        var contract = result.Document!;
        var mediaPath = Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "002-files-avatars", "contracts", "openapi.yaml");
        var yaml = (await File.ReadAllTextAsync(mediaPath, ct)).Replace("../../001-blog-engine-mvp/contracts/openapi.yaml#/components/schemas/Taxonomy", "'#/components/schemas/Taxonomy'", StringComparison.Ordinal);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(yaml));
        var media = await OpenApiDocument.LoadAsync(stream, "yaml", settings, ct); Assert.Empty(media.Diagnostic!.Errors);
        foreach (var (route, item) in media.Document!.Paths) { contract.Paths.Add(route, item); }
        foreach (var (name, schema) in media.Document.Components!.Schemas!) { contract.Components!.Schemas![name] = schema; }
        foreach (var (name, response) in media.Document.Components.Responses!) { contract.Components!.Responses![name] = response; }
        foreach (var (name, scheme) in media.Document.Components.SecuritySchemes!) { contract.Components!.SecuritySchemes![name] = scheme; }
        return JsonNode.Parse(await contract.SerializeAsync(OpenApiSpecVersion.OpenApi3_1, "json", cancellationToken: ct))!.AsObject();
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
            Assert.True(JsonNode.DeepEquals(Expand(expected, item?["parameters"]),
                Expand(actual, actual["paths"]?[path]?["parameters"])), path + " parameters");
            foreach (var (method, operation) in item!.AsObject())
            {
                if (method is not ("get" or "post" or "put" or "delete")) { continue; }
                var generated = actual["paths"]?[path]?[method]; Assert.NotNull(generated);
                foreach (var field in new[] { "operationId", "parameters", "requestBody", "responses", "security" })
                { Assert.True(JsonNode.DeepEquals(Expand(expected, operation?[field]), Expand(actual, generated[field])), $"{method} {path}: {field}"); }
                comparisons.Add(new { path, method, operationId = operation!["operationId"]!.GetValue<string>(), passed = true });
            }
        }
        foreach (var (section, items) in expected["components"]!.AsObject())
        {
            Assert.True(JsonNode.DeepEquals(items, actual["components"]![section]), "Components " + section);
        }
        var publicOperations = actual["paths"]!.AsObject().Where(p => p.Key.StartsWith("/api/", StringComparison.Ordinal) || p.Key.StartsWith("/health/", StringComparison.Ordinal))
            .SelectMany(p => p.Value!.AsObject().Where(o => o.Key is "get" or "post" or "put" or "delete")).ToArray();
        Assert.Equal(14, publicOperations.Count(o => IsMedia(o.Value!["operationId"]!.GetValue<string>())));
        Assert.Equal(comparisons.Count, publicOperations.Length);
        var mediaCodes = actual["components"]!["schemas"]!["Problem"]!["properties"]!["code"]!["enum"]!.AsArray()
            .Select(c => c!.GetValue<string>()).ToArray();
        Assert.Contains("idempotency_conflict", mediaCodes); Assert.Contains("content_conflict", mediaCodes); Assert.Contains("quota_exceeded", mediaCodes);
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
