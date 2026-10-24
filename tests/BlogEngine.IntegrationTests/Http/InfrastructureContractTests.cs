using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BlogEngine.IntegrationTests.Http;

[Trait("Category", "ConsumerContract")]
public sealed class InfrastructureContractTests
{
    [Fact]
    public async Task ProtocolFailuresHaveSafeStableProblemDetails()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        var cases = new (HttpMethod Method, string Path, string? Body, string? Media, int Status, string Code)[]
        {
            (HttpMethod.Get, "/missing", null, null, 404, "resource_not_found"),
            (HttpMethod.Post, "/health/live", null, null, 405, "method_not_allowed"),
            (HttpMethod.Get, "/__foundation/private", null, null, 401, "authentication_required"),
            (HttpMethod.Post, "/__foundation/json", "{", "application/json", 400, "invalid_request"),
            (HttpMethod.Post, "/__foundation/json", "{\"name\":\"ok\",\"admin\":true}", "application/json", 400, "invalid_request"),
            (HttpMethod.Post, "/__foundation/json", "{\"name\":null}", "application/json", 400, "invalid_request"),
            (HttpMethod.Post, "/__foundation/json", "{\"name\":\"first\",\"name\":\"second\"}", "application/json", 400, "invalid_request"),
            (HttpMethod.Post, "/__foundation/json", "{}", "application/json", 400, "invalid_request"),
            (HttpMethod.Post, "/__foundation/json", "name", "text/plain", 415, "unsupported_media_type"),
            (HttpMethod.Post, "/__foundation/bodyless", "{}", "application/json", 400, "invalid_request"),
            (HttpMethod.Get, "/__foundation/id/not-a-uuid", null, null, 400, "invalid_request"),
            (HttpMethod.Get, "/__foundation/id/11111111-1111-1111-1111-111111111111?page=0", null, null, 400, "validation_failed"),
            (HttpMethod.Get, "/__foundation/id/11111111-1111-1111-1111-111111111111?page=bad", null, null, 400, "invalid_request"),
            (HttpMethod.Get, "/__foundation/error", null, null, 500, "internal_error"),
            (HttpMethod.Get, "/__foundation/cancel", null, null, 500, "internal_error"),
        };
        foreach (var item in cases)
        {
            using var request = new HttpRequestMessage(item.Method, item.Path);
            if (item.Body is not null) { request.Content = new StringContent(item.Body, Encoding.UTF8, item.Media!); }
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            await AssertProblem(response, item.Status, item.Code);
        }
        using var unacceptable = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        unacceptable.Headers.Accept.ParseAdd("text/html");
        using var notAcceptable = await client.SendAsync(unacceptable, TestContext.Current.CancellationToken);
        await AssertProblem(notAcceptable, 406, "not_acceptable");
        using var large = await client.PostAsync("/__foundation/json", new StringContent("{\"name\":\"" + new string('x', 8 * 1024 * 1024) + "\"}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        await AssertProblem(large, 413, "request_too_large");
        var far = await client.GetFromJsonAsync<JsonElement>("/__foundation/id/11111111-1111-1111-1111-111111111111?page=2147483647&pageSize=100", TestContext.Current.CancellationToken);
        Assert.Equal(214748364600L, far.GetProperty("offset").GetInt64());
    }

    [Fact]
    public async Task DecodedMarkdownAndChunkedEnvelopeHaveSeparateSizeLimits()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        // JSON escaping fits below 8 MiB but decodes to exactly 1 MiB of Markdown.
        using var exact = await client.PostAsync("/__foundation/markdown", new StringContent(
            "{\"markdown\":\"" + string.Concat(Enumerable.Repeat("\\u0061", 1_048_576)) + "\"}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, exact.StatusCode);
        using var excessive = await client.PostAsJsonAsync("/__foundation/markdown", new { markdown = new string('a', 1_048_577) }, TestContext.Current.CancellationToken);
        await AssertProblem(excessive, 400, "validation_failed");
        using var nul = await client.PostAsJsonAsync("/__foundation/markdown", new { markdown = "\0" }, TestContext.Current.CancellationToken);
        await AssertProblem(nul, 400, "validation_failed");
        using var chunked = new HttpRequestMessage(HttpMethod.Post, "/__foundation/json")
        {
            Content = new UnknownLengthContent("{\"name\":\"" + new string('x', 8 * 1024 * 1024) + "\"}"),
        };
        using var response = await client.SendAsync(chunked, TestContext.Current.CancellationToken);
        await AssertProblem(response, 413, "request_too_large");
        using var bodyless = new HttpRequestMessage(HttpMethod.Post, "/__foundation/bodyless") { Content = new UnknownLengthContent("{}") };
        using var unexpected = await client.SendAsync(bodyless, TestContext.Current.CancellationToken);
        await AssertProblem(unexpected, 400, "invalid_request");
    }

    [Fact]
    public async Task LogsExcludeInputCredentialsAndExceptionMessagesAndCorrelateOneUnexpectedError()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        const string secret = "input-sentinel-never-log";
        using var write = await client.PostAsJsonAsync("/__foundation/json", new { name = secret }, TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__foundation/error?secret=" + secret);
        request.Headers.Add("X-Anonymous-Visitor", secret);
        request.Headers.Authorization = new("Bearer", identity.CreateToken());
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await AssertProblem(response, 500, "internal_error");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = factory.Logs.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error).ToArray();
        Assert.Single(errors);
        Assert.Contains(problem.GetProperty("traceId").GetString()!, errors[0].Message, StringComparison.Ordinal);
        Assert.All(factory.Logs.Entries, entry =>
        {
            Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive exception sentinel", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(request.Headers.Authorization.Parameter!, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT ", entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CorsAllowsOnlyConfiguredOriginsWithoutCredentialsAndDevelopmentPublishesOpenApi31()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Api:AllowedOrigins:0"] = "https://client.example" })));
        using var client = configured.CreateClient();
        foreach (var origin in new[] { "https://client.example", "https://other.example" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/__foundation/private");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "GET");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(origin == "https://client.example", response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.StartsWith("3.1.", document.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        using var swagger = await client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] bytes;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        public UnknownLengthContent(string value)
        {
            bytes = Encoding.UTF8.GetBytes(value);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
    }

    [Fact]
    public async Task VersionHeaderCacheAndOptionalBearerFollowTheContract()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        foreach (var header in new[] { null, "wrong", Guid.NewGuid() + "," + Guid.NewGuid() })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/__foundation/version");
            if (header is not null) { request.Headers.TryAddWithoutValidation("X-Resource-Version", header); }
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            await AssertProblem(response, 400, "validation_failed");
        }
        using var stale = new HttpRequestMessage(HttpMethod.Post, "/__foundation/version");
        stale.Headers.Add("X-Resource-Version", Guid.NewGuid().ToString());
        using var conflict = await client.SendAsync(stale, TestContext.Current.CancellationToken);
        await AssertProblem(conflict, 409, "concurrency_conflict");
        using var anonymous = await client.GetAsync("/__foundation/public", TestContext.Current.CancellationToken);
        Assert.True(anonymous.Headers.CacheControl?.NoStore);
        Assert.True(anonymous.Headers.CacheControl?.Private);
        Assert.Contains("Authorization", anonymous.Headers.Vary);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "bad");
        using var invalid = await client.GetAsync("/__foundation/public", TestContext.Current.CancellationToken);
        await AssertProblem(invalid, 401, "invalid_token");
        using var protectedError = await client.GetAsync("/__foundation/private", TestContext.Current.CancellationToken);
        Assert.True(protectedError.Headers.CacheControl?.NoStore);
        client.DefaultRequestHeaders.Authorization = null;
        using var utc = await client.GetAsync("/__foundation/time", TestContext.Current.CancellationToken);
        Assert.Contains("1970-01-01T00:00:00Z", await utc.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using var protectedMe = await client.GetAsync("/api/v1/me", TestContext.Current.CancellationToken);
        await AssertProblem(protectedMe, 401, "authentication_required");
    }

    internal static async Task AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(status, doc.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("sentinel", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", raw, StringComparison.Ordinal);
    }
}
