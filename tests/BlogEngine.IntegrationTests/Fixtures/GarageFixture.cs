using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Volumes;

namespace BlogEngine.IntegrationTests.Fixtures;

public sealed class GarageFixture : IAsyncLifetime
{
    public const string SentinelKey = "_blog/health-v1";
    public const int SentinelLength = 29;
    public const string SentinelSha256 = "e85f6fe5e6589eee2f0cf83bc9356352a1a94ef01ff75dc925b6487fe34bcf75";
    private readonly string adminToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly IVolume metadata = new VolumeBuilder().Build();
    private readonly IVolume data = new VolumeBuilder().Build();
    private IContainer? container;
    private readonly HttpClient admin = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private string accessKey = string.Empty;
    private string secretKey = string.Empty;
    private Uri adminEndpoint = null!;
    public string OriginalsBucket => "blog-originals";
    public string RenditionsBucket => "blog-renditions";
    public IReadOnlyList<string> Buckets => [OriginalsBucket, RenditionsBucket];
    public Uri Endpoint { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        try
        {
            await metadata.CreateAsync(ct); await data.CreateAsync(ct);
            container = new ContainerBuilder(GarageImage.Reference)
                .WithEnvironment("GARAGE_RPC_SECRET", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant())
                .WithEnvironment("GARAGE_ADMIN_TOKEN", adminToken)
                .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "garage.development.toml"), ct), "/etc/garage.toml")
                .WithVolumeMount(metadata, "/var/lib/garage/meta")
                .WithVolumeMount(data, "/var/lib/garage/data")
                .WithPortBinding(3900, true).WithPortBinding(3903, true)
                .WithCreateParameterModifier(parameters =>
                {
                    var ports = parameters.HostConfig?.PortBindings ?? throw new InvalidOperationException("Garage requires loopback port bindings.");
                    foreach (var bindings in ports.Values)
                    { foreach (var binding in bindings) { binding.HostIP = "127.0.0.1"; } }
                })
                // Garage's minimal image has no shell for Testcontainers' TCP probe.
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                    .ForPort(3903).ForPath("/v2/GetClusterStatus").WithHeader("Authorization", "Bearer " + adminToken)))
                .Build();
            await container.StartAsync(ct);
            ConfigureEndpoints();
            // The versioned JSON API is used instead of parsing unstable human CLI output.
            var status = await AdminAsync(HttpMethod.Get, "GetClusterStatus", null, ct);
            var node = status.GetProperty("nodes")[0].GetProperty("id").GetString()!;
            await AdminAsync(HttpMethod.Post, "UpdateClusterLayout", new
            { roles = new[] { new { id = node, zone = "integration-only", capacity = 1_000_000_000L, tags = Array.Empty<string>() } } }, ct);
            await AdminAsync(HttpMethod.Post, "ApplyClusterLayout", new { version = 1 }, ct);
            await ProvisionAsync(ct);
        }
        catch { await DisposeAsync(); throw; }
    }

    private void ConfigureEndpoints()
    {
        Endpoint = new Uri($"http://127.0.0.1:{container!.GetMappedPublicPort(3900)}/");
        adminEndpoint = new Uri($"http://127.0.0.1:{container.GetMappedPublicPort(3903)}/");
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
    }

    public AmazonS3Client CreateClient() => new(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
    {
        ServiceURL = Endpoint.AbsoluteUri,
        AuthenticationRegion = "garage",
        ForcePathStyle = true,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        Timeout = TimeSpan.FromSeconds(5),
        MaxErrorRetry = 0,
    });

    public IReadOnlyDictionary<string, string?> Configuration => new Dictionary<string, string?>
    {
        ["Media:Storage:Endpoint"] = Endpoint.AbsoluteUri,
        ["Media:Storage:Region"] = "garage",
        ["Media:Storage:OriginalsBucket"] = OriginalsBucket,
        ["Media:Storage:RenditionsBucket"] = RenditionsBucket,
        ["Media:Storage:AccessKey"] = accessKey,
        ["Media:Storage:SecretKey"] = secretKey,
        ["Media:Storage:AllowLocalHttp"] = "true",
    };

    public async Task ProvisionAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(AppContext.BaseDirectory, "ProvisionFixture.ps1"),
                     "-AdminEndpoint", adminEndpoint.AbsoluteUri, "-S3Endpoint", Endpoint.AbsoluteUri }) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell provisioning could not start.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, string>
            { ["Blog.Media.AdminToken"] = adminToken, ["Blog.Media.AccessKey"] = accessKey, ["Blog.Media.SecretKey"] = secretKey }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var safeOutput = await output;
            // Never include the stderr/provider body in a test failure.
            await error;
            if (process.ExitCode != 0) { throw new InvalidOperationException("Media provisioning failed; inspect the script stage using disposable credentials."); }
            using var descriptor = JsonDocument.Parse(safeOutput);
            Assert.Equal(SentinelLength, descriptor.RootElement.GetProperty("SentinelLength").GetInt32());
            Assert.Equal(SentinelSha256, descriptor.RootElement.GetProperty("SentinelSha256").GetString());
            var keys = await AdminAsync(HttpMethod.Get, "ListKeys?limit=100", null, ct);
            var keyId = keys.EnumerateArray().Single().GetProperty("id").GetString();
            var key = await AdminAsync(HttpMethod.Get, "GetKeyInfo?id=" + keyId + "&showSecretKey=true", null, ct);
            accessKey = key.GetProperty("accessKeyId").GetString()!;
            secretKey = key.GetProperty("secretAccessKey").GetString()!;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }

    public async Task AssertRuntimePermissionsAsync(CancellationToken ct)
    {
        var key = await AdminAsync(HttpMethod.Get, "GetKeyInfo?id=" + accessKey, null, ct);
        Assert.False(key.GetProperty("permissions").GetProperty("createBucket").GetBoolean());
        Assert.Equal(2, key.GetProperty("buckets").GetArrayLength());
        foreach (var bucket in key.GetProperty("buckets").EnumerateArray())
        {
            var permissions = bucket.GetProperty("permissions");
            Assert.True(permissions.GetProperty("read").GetBoolean()); Assert.True(permissions.GetProperty("write").GetBoolean());
            Assert.False(permissions.GetProperty("owner").GetBoolean());
            var info = await AdminAsync(HttpMethod.Get, "GetBucketInfo?id=" + bucket.GetProperty("id").GetString(), null, ct);
            Assert.False(info.GetProperty("websiteAccess").GetBoolean());
            Assert.Single(info.GetProperty("keys").EnumerateArray());
        }
    }

    public async Task VerifyProvisioningGuardsAsync(CancellationToken ct)
    {
        var bucket = await AdminAsync(HttpMethod.Get, "GetBucketInfo?globalAlias=" + OriginalsBucket, null, ct);
        var bucketId = bucket.GetProperty("id").GetString()!;
        await AdminAsync(HttpMethod.Post, "UpdateKey?id=" + accessKey, new { allow = new { createBucket = true } }, ct);
        await AdminAsync(HttpMethod.Post, "AllowBucketKey", new { bucketId, accessKeyId = accessKey, permissions = new { owner = true } }, ct);
        await ProvisionAsync(ct);
        await AssertRuntimePermissionsAsync(ct);

        var outsider = await AdminAsync(HttpMethod.Post, "CreateKey", new { name = "isolated-outsider" }, ct);
        var outsiderId = outsider.GetProperty("accessKeyId").GetString()!;
        try
        {
            await AdminAsync(HttpMethod.Post, "AllowBucketKey", new { bucketId, accessKeyId = outsiderId, permissions = new { read = true } }, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(ct));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AdminAsync(HttpMethod.Post, "DeleteKey?id=" + outsiderId, new { }, cleanup.Token);
        }

        var unrelated = await AdminAsync(HttpMethod.Post, "CreateBucket", new { globalAlias = "unrelated-fixture" }, ct);
        var unrelatedId = unrelated.GetProperty("id").GetString()!;
        try
        {
            using var s3 = CreateClient();
            var denied = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.GetObjectAsync("unrelated-fixture", SentinelKey, ct));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
            await AdminAsync(HttpMethod.Post, "AllowBucketKey", new { bucketId = unrelatedId, accessKeyId = accessKey, permissions = new { read = true } }, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(ct));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AdminAsync(HttpMethod.Post, "DenyBucketKey", new { bucketId = unrelatedId, accessKeyId = accessKey, permissions = new { read = true } }, cleanup.Token);
            await AdminAsync(HttpMethod.Post, "DeleteBucket?id=" + unrelatedId, new { }, cleanup.Token);
        }
        await ProvisionAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => container!.StopAsync(ct);
    public async Task RestartAsync(CancellationToken ct)
    {
        await container!.StartAsync(ct);
        // Docker may reassign ephemeral host ports; callers create fresh clients after restart.
        ConfigureEndpoints();
        using var response = await admin.GetAsync(new Uri(adminEndpoint, "health"), ct);
        Assert.True(response.IsSuccessStatusCode);
    }

    private async Task<JsonElement> AdminAsync(HttpMethod method, string operation, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(adminEndpoint, "v2/" + operation)) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await admin.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) { throw new InvalidOperationException($"Garage setup admin call failed (HTTP {(int)response.StatusCode})."); }
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async ValueTask DisposeAsync()
    {
        admin.Dispose();
        try { if (container is not null) { await container.DisposeAsync(); } }
        finally { try { await data.DisposeAsync(); } finally { await metadata.DisposeAsync(); } }
    }
}
