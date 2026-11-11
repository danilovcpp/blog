using System.Net;
using Microsoft.Extensions.Options;

namespace BlogEngine.Infrastructure.Media;

public sealed class MediaOptions
{
    public MediaStorageOptions Storage { get; set; } = new();
    public MediaSpoolOptions Spool { get; set; } = new();
    public MediaTransferOptions Transfers { get; set; } = new();
    public MediaProcessingOptions Processing { get; set; } = new();
    public MediaCleanupOptions Cleanup { get; set; } = new();
    public bool MaintenanceMode { get; set; }
}
public sealed class MediaStorageOptions
{
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "garage";
    public string OriginalsBucket { get; set; } = "blog-originals";
    public string RenditionsBucket { get; set; } = "blog-renditions";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public bool AllowLocalHttp { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(3);
}
public sealed class MediaSpoolOptions
{
    public string Directory { get; set; } = "";
    public long BudgetBytes { get; set; } = 1_073_741_824;
}
public sealed class MediaTransferOptions
{
    public int Concurrency { get; set; } = 4;
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
public sealed class MediaProcessingOptions
{
    public int Concurrency { get; set; } = 1;
    public int BatchSize { get; set; } = 20;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(180);
    public TimeSpan RenewalInterval { get; set; } = TimeSpan.FromSeconds(30);
}
public sealed class MediaCleanupOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    public int BatchSize { get; set; } = 100;
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(15);
    public int VerifiedTombstones { get; set; } = 10_000;
    public int VerifiedDailyRetirements { get; set; } = 1_000;
    public int VerifiedExpiredBacklog { get; set; } = 1_000;
    public int VerifiedDailyExpiries { get; set; } = 1_000;
    public TimeSpan ReconciliationWarningAge { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan PendingWarningAge { get; set; } = TimeSpan.FromHours(12);
}

public sealed class MediaOptionsValidator : IValidateOptions<MediaOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaOptions o)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(o.Storage.Endpoint, UriKind.Absolute, out var endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/" ||
            (endpoint.Scheme != "https" && !(o.Storage.AllowLocalHttp && endpoint.Scheme == "http" && PrivateHost(endpoint.Host))))
        { errors.Add("Media storage requires a trusted HTTPS endpoint or explicitly allowed local HTTP."); }
        if (o.Storage.Region != "garage" || !Bucket(o.Storage.OriginalsBucket) || !Bucket(o.Storage.RenditionsBucket) || o.Storage.OriginalsBucket == o.Storage.RenditionsBucket)
        { errors.Add("Media storage requires region garage and distinct valid buckets."); }
        if (!Secret(o.Storage.AccessKey) || !Secret(o.Storage.SecretKey)) { errors.Add("Media storage credentials must come from a protected provider."); }
        if (string.IsNullOrWhiteSpace(o.Spool.Directory) || !Path.IsPathFullyQualified(o.Spool.Directory) || o.Spool.BudgetBytes <= 0)
        { errors.Add("Media spool requires an absolute private directory and a positive budget."); }
        if (o.Transfers.Concurrency is < 1 or > 64 || o.Processing.Concurrency is < 1 or > 8 || o.Processing.BatchSize is < 1 or > 1000 || o.Cleanup.BatchSize is < 1 or > 1000)
        { errors.Add("Media concurrency or batch size is outside supported bounds."); }
        var times = new[] { o.Storage.RequestTimeout, o.Storage.ReadinessTimeout, o.Transfers.TotalTimeout,
            o.Processing.LeaseDuration, o.Processing.RenewalInterval, o.Cleanup.Interval, o.Cleanup.RetryMaxDelay,
            o.Cleanup.ReconciliationWarningAge, o.Cleanup.PendingWarningAge };
        if (times.Any(t => t <= TimeSpan.Zero || t > TimeSpan.FromHours(24)) || o.Processing.RenewalInterval >= o.Processing.LeaseDuration ||
            o.Storage.ReadinessTimeout > o.Storage.RequestTimeout || o.Cleanup.RetryMaxDelay > TimeSpan.FromMinutes(15))
        { errors.Add("Media timeouts, lease renewal or backoff are invalid."); }
        if (o.Cleanup.VerifiedTombstones <= 0 || o.Cleanup.VerifiedDailyRetirements <= 0 || o.Cleanup.VerifiedExpiredBacklog <= 0 || o.Cleanup.VerifiedDailyExpiries <= 0)
        { errors.Add("Media cleanup capacity profile must be positive."); }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool Secret(string value) => !string.IsNullOrWhiteSpace(value) && value.Length >= 8 &&
        !new[] { "placeholder", "changeme", "example", "<", "${" }.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));
    private static bool Bucket(string value) => value.Length is >= 3 and <= 63 && char.IsAsciiLetterOrDigit(value[0]) && char.IsAsciiLetterOrDigit(value[^1]) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') && !IPAddress.TryParse(value, out _);
    private static bool PrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "garage") { return true; }
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) { return false; }
        if (IPAddress.IsLoopback(address)) { return true; }
        var b = address.GetAddressBytes();
        return b.Length == 4 ? b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) : (b[0] & 0xfe) == 0xfc;
    }
}
