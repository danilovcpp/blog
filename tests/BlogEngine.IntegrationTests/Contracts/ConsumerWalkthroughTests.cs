using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.IntegrationTests.Fixtures;
using BlogEngine.IntegrationTests.Http;
using static BlogEngine.IntegrationTests.Fixtures.PublishingFixture;

namespace BlogEngine.IntegrationTests.Contracts;

[Trait("Category", "ConsumerContract")]
public sealed class ConsumerWalkthroughTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task DocumentedConsumerCompletesAllSevenStoriesAndReceivesOnlyPermittedFields()
    {
        var ct = TestContext.Current.CancellationToken; var contract = await OpenApiCompatibilityTests.ContractAsync(ct);
        await using var f = await StartAsync(postgres, ct);
        var author = await f.ActorAsync(LocalRole.Author, ct); var reader = await f.ActorAsync(LocalRole.Reader, ct);
        var admin = await f.ActorAsync(LocalRole.Admin, ct); var moderator = await f.ActorAsync(LocalRole.Moderator, ct);
        var covered = new HashSet<string>();

        async Task<JsonElement> Call(HttpClient client, string operationId, object? body = null, Guid? id = null, Guid? version = null, string? slug = null)
        {
            var endpoint = contract["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Where(m => m.Value?["operationId"]?.GetValue<string>() == operationId)
                .Select(m => (Path: p.Key, Method: m.Key, Operation: m.Value!))).Single();
            var path = endpoint.Path.Replace("{id}", id?.ToString("D"), StringComparison.Ordinal)
                .Replace("{commentId}", id?.ToString("D"), StringComparison.Ordinal).Replace("{slug}", slug, StringComparison.Ordinal);
            using var response = await SendAsync(client, new HttpMethod(endpoint.Method), path, version, body, ct);
            var status = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var definition = endpoint.Operation["responses"]![status]; Assert.NotNull(definition);
            Assert.True(response.IsSuccessStatusCode, operationId + ": " + await response.Content.ReadAsStringAsync(ct));
            covered.Add(operationId);
            var schema = definition["content"]?["application/json"]?["schema"];
            if (schema is null) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); return default; }
            var raw = await response.Content.ReadAsStringAsync(ct); var json = JsonNode.Parse(raw)!;
            Validate(contract, schema, json, operationId);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var doc = JsonDocument.Parse(raw); return doc.RootElement.Clone();
        }

        await Call(reader, "getMe");
        var category = await Call(admin, "createCategory", new { name = "Walkthrough", slug = "walkthrough" });
        var tag = await Call(admin, "createTag", new { name = "Walkthrough", slug = "walkthrough" });
        foreach (var (kind, item) in new[] { ("Category", category), ("Tag", tag) })
        {
            await Call(f.Anonymous, "get" + kind, id: item.GetProperty("id").GetGuid());
            await Call(f.Anonymous, "list" + (kind == "Category" ? "Categories" : "Tags"));
        }
        var draft = await Call(author, "createPublication", Content("consumer-walkthrough", "private-draft-sentinel", [category.GetProperty("id").GetGuid()], [tag.GetProperty("id").GetGuid()]));
        var pubId = draft.GetProperty("id").GetGuid();
        using (var closed = await f.Anonymous.GetAsync($"/api/v1/publications/{pubId}", ct)) { await InfrastructureContractTests.AssertProblem(closed, 404, "resource_not_found"); }
        await Call(author, "getWorkspacePublication", id: pubId); await Call(author, "listWorkspacePublications");
        var updated = await Call(author, "updatePublication", Content("consumer-walkthrough", "# public <script>untrusted</script>", [category.GetProperty("id").GetGuid()], [tag.GetProperty("id").GetGuid()]), pubId, draft.GetProperty("version").GetGuid());
        var published = await Call(author, "publishPublication", id: pubId, version: updated.GetProperty("version").GetGuid());
        await Call(f.Anonymous, "getPublication", id: pubId); await Call(reader, "getPublicationBySlug", slug: "consumer-walkthrough");
        var feed = await Call(reader, "listPublications"); Assert.DoesNotContain("markdown", feed.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await Call(reader, "getCounters", id: pubId); await Call(reader, "setLike", id: pubId); await Call(reader, "setLike", id: pubId);
        await Call(reader, "removeLike", id: pubId);
        var view = await Call(f.Anonymous, "registerView", id: pubId); Assert.NotNull(view.GetProperty("anonymousVisitorId").GetString());
        await Call(reader, "registerView", id: pubId);
        var comment = await Call(reader, "createComment", new { content = "hidden-content-sentinel" }, pubId);
        var commentId = comment.GetProperty("id").GetGuid();
        await Call(author, "createComment", new { content = "visible reply", parentId = commentId }, pubId);
        await Call(reader, "getCommentEditContext", id: commentId);
        comment = await Call(reader, "updateComment", new { content = "hidden-content-sentinel" }, commentId, comment.GetProperty("version").GetGuid());
        var hidden = await Call(moderator, "hideComment", id: commentId, version: comment.GetProperty("version").GetGuid());
        var tree = await Call(f.Anonymous, "listComments", id: pubId); Assert.DoesNotContain("hidden-content-sentinel", tree.GetRawText(), StringComparison.Ordinal);
        await Call(moderator, "listModerationComments");
        var restored = await Call(moderator, "restoreComment", id: commentId, version: hidden.GetProperty("version").GetGuid());
        await Call(reader, "deleteComment", id: commentId, version: restored.GetProperty("version").GetGuid());
        var unpublished = await Call(author, "unpublishPublication", id: pubId, version: published.GetProperty("version").GetGuid());
        await Call(author, "deletePublication", id: pubId, version: unpublished.GetProperty("version").GetGuid());
        foreach (var (kind, item) in new[] { ("Category", category), ("Tag", tag) })
        {
            var renamed = await Call(admin, "rename" + kind, new { name = "Renamed" }, item.GetProperty("id").GetGuid(), item.GetProperty("version").GetGuid());
            await Call(admin, "delete" + kind, id: item.GetProperty("id").GetGuid(), version: renamed.GetProperty("version").GetGuid());
        }
        await Call(f.Anonymous, "healthLive"); await Call(f.Anonymous, "healthReady");
        Assert.Equal(35, covered.Count);
        foreach (var entry in f.Factory.Logs.Entries)
        {
            foreach (var forbidden in new[] { "hidden-content-sentinel", "private-draft-sentinel", view.GetProperty("anonymousVisitorId").GetString()!, "<script>" })
            { Assert.DoesNotContain(forbidden, entry.Message, StringComparison.Ordinal); }
        }
        await File.WriteAllTextAsync(ArtifactPath("consumer-walkthrough.json"), JsonSerializer.Serialize(new { status = "passed", operations = covered.Order().ToArray() }), ct);
    }

    internal static void Validate(JsonNode contract, JsonNode schema, JsonNode? value, string path)
    {
        if (schema["$ref"] is { } reference)
        {
            var target = contract;
            foreach (var segment in reference.GetValue<string>().Split('/').Skip(1)) { target = target![segment]; }
            Validate(contract, target!, value, path); return;
        }
        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (schema[keyword] is not JsonArray branches) { continue; }
            var matching = 0;
            foreach (var branch in branches)
            {
                try { Validate(contract, branch!, value, path); matching++; }
                catch (Xunit.Sdk.XunitException) { }
            }
            Assert.True(keyword == "oneOf" ? matching == 1 : matching > 0, path + " " + keyword);
        }
        if (schema["type"] is { } type)
        {
            var types = type is JsonArray array ? array.Select(t => t!.GetValue<string>()).ToArray() : [type.GetValue<string>()];
            var actualType = value is null ? "null" : value.GetValueKind() switch
            { JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "integer" };
            Assert.Contains(actualType, types);
        }
        if (schema["enum"] is JsonArray allowed) { Assert.Contains(allowed, v => JsonNode.DeepEquals(v, value)); }
        if (schema["const"] is { } constant) { Assert.True(JsonNode.DeepEquals(constant, value), path + " const"); }
        if (value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String)
        {
            var text = scalar.GetValue<string>(); var length = text.EnumerateRunes().Count();
            if (schema["minLength"] is { } minimum) { Assert.True(length >= minimum.GetValue<int>(), path + " minLength"); }
            if (schema["maxLength"] is { } maximum) { Assert.True(length <= maximum.GetValue<int>(), path + " maxLength"); }
            if (schema["pattern"] is { } pattern) { Assert.Matches(pattern.GetValue<string>(), text); }
            if (schema["format"]?.GetValue<string>() == "uuid") { Assert.True(Guid.TryParseExact(text, "D", out _), path + " uuid"); }
            if (schema["format"]?.GetValue<string>() == "date-time") { Assert.True(DateTimeOffset.TryParse(text, out _) && text.EndsWith('Z'), path + " UTC instant"); }
        }
        if (value is JsonValue number && number.GetValueKind() == JsonValueKind.Number)
        {
            var numeric = number.Deserialize<decimal>();
            if (schema["minimum"] is { } minimum) { Assert.True(numeric >= minimum.GetValue<decimal>(), path + " minimum"); }
            if (schema["maximum"] is { } maximum) { Assert.True(numeric <= maximum.GetValue<decimal>(), path + " maximum"); }
        }
        if (value is JsonObject obj)
        {
            foreach (var required in schema["required"]?.AsArray() ?? []) { Assert.True(obj.ContainsKey(required!.GetValue<string>()), path + " required " + required); }
            var properties = schema["properties"]?.AsObject();
            foreach (var (key, child) in obj)
            {
                if (properties?.TryGetPropertyValue(key, out var property) == true) { Validate(contract, property!, child, path + "." + key); }
                else if (schema["additionalProperties"] is JsonValue additional) { Assert.True(additional.GetValue<bool>(), path + " unknown " + key); }
            }
        }
        if (value is JsonArray items && schema["items"] is { } itemSchema)
        {
            if (schema["maxItems"] is { } maximum) { Assert.True(items.Count <= maximum.GetValue<int>(), path + " maxItems"); }
            if (schema["minItems"] is { } minimum) { Assert.True(items.Count >= minimum.GetValue<int>(), path + " minItems"); }
            foreach (var item in items) { Validate(contract, itemSchema, item, path + "[]"); }
        }
    }
}
