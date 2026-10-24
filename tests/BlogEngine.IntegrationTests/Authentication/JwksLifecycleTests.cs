using System.Net;
using System.Text.Json;
using BlogEngine.IntegrationTests.Fixtures;

namespace BlogEngine.IntegrationTests.Authentication;

[Trait("Category", "Authentication")]
public sealed class JwksLifecycleTests
{
    [Fact]
    public async Task RotationRemovalExpiryAndFailureCooldownHaveDistinctOutcomes()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        var old = identity.CreateToken();
        await JwtPipelineTests.AssertToken(client, old, HttpStatusCode.OK);
        identity.RotateKey();
        var current = identity.CreateToken();
        await JwtPipelineTests.AssertToken(client, current, HttpStatusCode.Unauthorized);
        Assert.Equal(1, identity.RequestCount);
        identity.Time.Advance(TimeSpan.FromSeconds(30));
        await JwtPipelineTests.AssertToken(client, current, HttpStatusCode.OK);
        await JwtPipelineTests.AssertToken(client, old, HttpStatusCode.Unauthorized);
        Assert.Equal(2, identity.RequestCount);
        identity.SetResponse("failure", HttpStatusCode.ServiceUnavailable);
        await JwtPipelineTests.AssertToken(client, current, HttpStatusCode.OK);
        identity.Time.Advance(TimeSpan.FromSeconds(300));
        await JwtPipelineTests.AssertToken(client, current, HttpStatusCode.ServiceUnavailable);
        await JwtPipelineTests.AssertToken(client, current, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(3, identity.RequestCount);
        identity.ResetResponse();
        identity.Time.Advance(TimeSpan.FromSeconds(30));
        await JwtPipelineTests.AssertToken(client, current, HttpStatusCode.OK);
        Assert.Equal(4, identity.RequestCount);
    }

    [Fact]
    public async Task ConcurrentFetchIsSingleFlightAndACancelledWaiterDoesNotCancelOtherRequests()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        identity.RequestEntered = entered;
        identity.ResponseGate = release.Task;
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        var jwt = identity.CreateToken();
        var requests = Enumerable.Range(0, 100).Select(_ => JwtPipelineTests.AssertToken(client, jwt, HttpStatusCode.OK)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        using var message = new HttpRequestMessage(HttpMethod.Get, "/__foundation/token");
        message.Headers.Authorization = new("Bearer", jwt);
        var aborted = client.SendAsync(message, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
        release.SetResult();
        await Task.WhenAll(requests);
        Assert.Equal(1, identity.RequestCount);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("keys33")]
    [InlineData("duplicate")]
    [InlineData("private")]
    [InlineData("malformed")]
    [InlineData("redirect")]
    public async Task InvalidJwksCannotReplaceFreshKeysAndFetchFailuresAreBounded(string scenario)
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        var valid = identity.CreateToken();
        await JwtPipelineTests.AssertToken(client, valid, HttpStatusCode.OK);
        using var original = JsonDocument.Parse(identity.ResponseBody);
        var key = original.RootElement.GetProperty("keys")[0].GetRawText();
        identity.RotateKey();
        var unknown = identity.CreateToken();
        var body = scenario switch
        {
            "oversize" => new string(' ', 262145),
            "keys33" => "{\"keys\":[" + string.Join(',', Enumerable.Repeat(key, 33)) + "]}",
            "duplicate" => "{\"keys\":[" + key + "," + key + "]}",
            "private" => "{\"keys\":[" + key[..^1] + ",\"d\":\"secret\"}]}",
            _ => "invalid-json",
        };
        identity.SetResponse(body, scenario == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK);
        identity.RedirectLocation = "http://127.0.0.1:1/redirect-must-not-be-followed";
        identity.Time.Advance(TimeSpan.FromSeconds(30));
        await JwtPipelineTests.AssertToken(client, unknown, HttpStatusCode.ServiceUnavailable);
        await JwtPipelineTests.AssertToken(client, valid, HttpStatusCode.OK);
        await JwtPipelineTests.AssertToken(client, unknown, HttpStatusCode.Unauthorized);
        Assert.Equal(2, identity.RequestCount);
    }

    [Fact]
    public async Task ShortMaxAgeExpiresWithoutExtendingLastKnownGoodAndTimeoutIsFiveSeconds()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        identity.MaxAgeSeconds = 10;
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        var jwt = identity.CreateToken();
        await JwtPipelineTests.AssertToken(client, jwt, HttpStatusCode.OK);
        identity.Time.Advance(TimeSpan.FromSeconds(10));
        await JwtPipelineTests.AssertToken(client, jwt, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, identity.RequestCount);
        identity.Time.Advance(TimeSpan.FromSeconds(20));
        identity.ResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await JwtPipelineTests.AssertToken(client, jwt, HttpStatusCode.ServiceUnavailable).WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        await JwtPipelineTests.AssertToken(client, jwt, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(2, identity.RequestCount);
    }

    [Fact]
    public async Task RedirectNeverContactsAnotherJwksServerAndFreshnessNeverExceedsFiveMinutes()
    {
        await using var identity = new JwksFixture();
        await identity.InitializeAsync();
        await using var other = new JwksFixture();
        await other.InitializeAsync();
        identity.SetResponse("redirect", HttpStatusCode.Redirect);
        identity.RedirectLocation = other.JwksUrl.AbsoluteUri;
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, identity);
        using var client = factory.CreateClient();
        var jwt = identity.CreateToken();
        await JwtPipelineTests.AssertToken(client, jwt, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(0, other.RequestCount);
        identity.RedirectLocation = null;
        identity.ResetResponse();
        identity.MaxAgeSeconds = 900;
        identity.Time.Advance(TimeSpan.FromSeconds(30));
        await JwtPipelineTests.AssertToken(client, jwt, HttpStatusCode.OK);
        identity.SetResponse("down", HttpStatusCode.ServiceUnavailable);
        identity.Time.Advance(TimeSpan.FromSeconds(300));
        await JwtPipelineTests.AssertToken(client, jwt, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(3, identity.RequestCount);
    }
}
