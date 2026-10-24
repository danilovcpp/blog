using System.Net;
using BlogEngine.IntegrationTests.Http;

namespace BlogEngine.IntegrationTests.Fixtures;

public static class AuthorizationMatrix
{
    public static async Task AssertAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        if (status == HttpStatusCode.OK || status == HttpStatusCode.Created) { Assert.Equal(status, response.StatusCode); return; }
        var code = status switch
        {
            HttpStatusCode.Unauthorized => "authentication_required",
            HttpStatusCode.Forbidden => "forbidden",
            HttpStatusCode.NotFound => "resource_not_found",
            HttpStatusCode.Conflict => "concurrency_conflict",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        await InfrastructureContractTests.AssertProblem(response, (int)status, code);
    }
}
