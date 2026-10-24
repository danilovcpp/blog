using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlogEngine.Performance;

public sealed record Attempt(int Sequence, double Milliseconds, int Bytes, string? Error, bool Drain, RequestTemplate Template, bool Partial);
public sealed record LatencySummary(int Attempts, int Failures, double P50, double P95, double P99, double Max, double Rps, long Bytes, bool Passed);

public sealed class ReadBenchmark(RequestPlan plan, BenchmarkIdentityFixture identity, Uri api, HttpMessageHandler? transport = null)
{
    public static LatencySummary Summarize(IEnumerable<Attempt> source, double seconds)
    {
        var attempts = source.ToArray(); var values = attempts.Select(a => a.Milliseconds).Order().ToArray();
        double Percentile(double p) => values.Length == 0 ? 0 : values[Math.Max(0, (int)Math.Ceiling(p * values.Length) - 1)];
        var failures = attempts.Count(a => a.Error is not null);
        return new(attempts.Length, failures, Percentile(.5), Percentile(.95), Percentile(.99), values.LastOrDefault(), attempts.Length / seconds,
            attempts.Sum(a => (long)a.Bytes), attempts.Length > 0 && Percentile(.95) <= 1000 && Percentile(.99) <= 2000 && failures / (double)attempts.Length <= .01);
    }
    public async Task<bool> RunAsync(string output, bool diagnostic, CancellationToken ct)
    {
        Directory.CreateDirectory(output);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 100, ConnectTimeout = TimeSpan.FromSeconds(5), UseCookies = false };
        using var client = new HttpClient(transport ?? handler) { BaseAddress = api, Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(ct); preparation.CancelAfter(TimeSpan.FromSeconds(5));
        using (var ready = await client.GetAsync("/health/ready", preparation.Token)) { ready.EnsureSuccessStatusCode(); }
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var metrics = new ConcurrentQueue<object>();
        var sampler = SampleAsync(metrics, running.Token);
        try
        {
            Console.WriteLine(diagnostic ? "Diagnostic warmup (not SC-005 acceptance)" : "SC-005 warmup: 120 seconds, 100 VU");
            await WindowAsync(client, diagnostic ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(2), ct);
            Console.WriteLine(diagnostic ? "Diagnostic measurement" : "SC-005 measurement: 600 seconds, 100 VU");
            var startedAt = DateTimeOffset.UtcNow;
            var result = await WindowAsync(client, diagnostic ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(10), ct);
            var endedAt = DateTimeOffset.UtcNow;
            var groups = result.Attempts.GroupBy(a => a.Template.Kind == "detail" ? "detail" : $"{a.Template.Sort}/{a.Template.Size}/{(a.Template.Shape != "none" ? "filtered" : "unfiltered")}")
                .ToDictionary(g => g.Key, g => new { window = Summarize(g.Where(a => !a.Drain), result.WindowSeconds), total = Summarize(g, result.TotalSeconds) });
            var all = Summarize(result.Attempts, result.TotalSeconds); var window = Summarize(result.Attempts.Where(a => !a.Drain), result.WindowSeconds);
            var complete = result.Attempts.Count % 400 == 0 && result.Attempts.Select(a => a.Sequence).Distinct().Count() == result.Attempts.Count;
            var variantsPass = groups.Count == 9 && groups.Values.All(g => g.window.Passed && g.total.Passed);
            var environment = ValidateEnvironment(output, result.WindowSeconds, startedAt, endedAt);
            var queryEvidence = ValidateQueryEvidence(output);
            var responseContract = result.Attempts.All(a => a.Error is not ("invalid_payload" or "cache_contract"));
            var passed = !diagnostic && complete && responseContract && all.Passed && window.Passed && variantsPass && environment && queryEvidence;
            var diagnosticGroups = result.Attempts.GroupBy(a => $"{a.Template.Kind}/{a.Template.BodyBytes}/{a.Template.Tier}/{(a.Template.Outcome != "nonempty" ? a.Template.Outcome : a.Partial ? "partial" : "full")}")
                .ToDictionary(g => g.Key, g => Summarize(g, result.TotalSeconds));
            await File.WriteAllTextAsync(Path.Combine(output, "read-report.json"), JsonSerializer.Serialize(new
            {
                status = passed ? "awaiting_dataset_verification" : "blocked_or_failed",
                acceptance = !diagnostic,
                profile = SeedDataset.Profile,
                virtualUsers = 100,
                result.WindowSeconds,
                result.TotalSeconds,
                drainSeconds = result.TotalSeconds - result.WindowSeconds,
                completeBlocks = complete,
                result.SequenceChecksum,
                plan.CatalogChecksum,
                plan.TemplateChecksum,
                window,
                total = all,
                variants = groups,
                diagnostics = diagnosticGroups,
                quotas = result.Attempts.GroupBy(a => a.Template).Select(g => new { template = g.Key, count = g.Count() }),
                errors = result.Attempts.Where(a => a.Error is not null).GroupBy(a => a.Error).ToDictionary(g => g.Key!, g => g.Count()),
                histogramMilliseconds = new[] { 10, 25, 50, 100, 250, 500, 1000, 2000, 5000, int.MaxValue }.Select(bound => new { upperBound = bound, count = result.Attempts.Count(a => a.Milliseconds <= bound) }),
                environmentEvidencePassed = environment,
                queryEvidencePassed = queryEvidence,
                responseContractPassed = responseContract,
                generatorMetrics = metrics.ToArray(),
            }, BenchmarkManifest.Json), ct);
            return passed;
        }
        finally { running.Cancel(); await sampler; }
    }
    private async Task<(List<Attempt> Attempts, double WindowSeconds, double TotalSeconds, string SequenceChecksum)> WindowAsync(HttpClient client, TimeSpan duration, CancellationToken ct)
    {
        var attempts = new ConcurrentQueue<Attempt>(); var clock = Stopwatch.StartNew(); var sync = new object(); var next = 0;
        using var sequenceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(duration + TimeSpan.FromSeconds(30));
        async Task Worker()
        {
            while (true)
            {
                PlannedRequest request; int index;
                lock (sync)
                {
                    if (next % 400 == 0 && clock.Elapsed >= duration) { return; }
                    index = next++; request = plan.Get(index / 400, index % 400);
                    sequenceHash.AppendData(Encoding.UTF8.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + request.Path + "|" + request.Template.Bearer + "|" + request.User + "\n"));
                }
                using var message = new HttpRequestMessage(HttpMethod.Get, request.Path);
                if (request.Template.Bearer) { message.Headers.Authorization = new("Bearer", identity.Token(request.User)); }
                var timer = Stopwatch.StartNew(); string? error = null; var bytes = 0; var partial = false;
                var completedAt = TimeSpan.Zero;
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
                    var body = await response.Content.ReadAsByteArrayAsync(requestTimeout.Token); bytes = body.Length; timer.Stop(); completedAt = clock.Elapsed;
                    if (response.StatusCode != HttpStatusCode.OK) { error = "http_" + (int)response.StatusCode; }
                    else
                    {
                        using var json = JsonDocument.Parse(body); plan.ValidateResponse(request, json.RootElement);
                        if (request.Publication is null) { partial = request.Expected.Length > 0 && request.Expected.Length < request.Template.Size; }
                        if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Headers.CacheControl?.NoStore != true || response.Headers.CacheControl.Private != true || !response.Headers.Vary.Contains("Authorization")) { error = "cache_contract"; }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = "timeout"; timer.Stop(); }
                catch (HttpRequestException) { error = "network"; timer.Stop(); }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
                { error = "invalid_payload"; timer.Stop(); }
                ct.ThrowIfCancellationRequested();
                attempts.Enqueue(new(index, error == "timeout" ? 5000 : timer.Elapsed.TotalMilliseconds, bytes, error, (completedAt == TimeSpan.Zero ? clock.Elapsed : completedAt) >= duration, request.Template, partial));
                if (deadline.IsCancellationRequested) { throw new InvalidOperationException("The final block did not drain within 30 seconds."); }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Worker()));
        return (attempts.OrderBy(a => a.Sequence).ToList(), duration.TotalSeconds, clock.Elapsed.TotalSeconds, Convert.ToHexStringLower(sequenceHash.GetHashAndReset()));
    }
    private static async Task SampleAsync(ConcurrentQueue<object> samples, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); using var process = Process.GetCurrentProcess();
            while (await timer.WaitForNextTickAsync(ct))
            {
                process.Refresh();
                var io = OperatingSystem.IsLinux() ? await File.ReadAllTextAsync("/proc/self/io", ct) : "unavailable on this OS";
                samples.Enqueue(new { at = DateTimeOffset.UtcNow, cpuSeconds = process.TotalProcessorTime.TotalSeconds, residentBytes = process.WorkingSet64, io });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private static bool ValidateEnvironment(string output, double seconds, DateTimeOffset start, DateTimeOffset end)
    {
        var file = Path.Combine(output, "stand-evidence.json"); if (!File.Exists(file)) { return false; }
        using var doc = JsonDocument.Parse(File.ReadAllText(file)); var e = doc.RootElement;
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64 || seconds < 600) { return false; }
        foreach (var (role, cpus, memory) in new[] { ("api", 4, 8), ("postgres", 8, 16), ("generator", 2, 4) })
        {
            var host = e.GetProperty(role);
            if (host.GetProperty("vCpu").GetInt32() != cpus || host.GetProperty("memoryGiB").GetInt32() != memory || !host.GetProperty("dedicated").GetBoolean()
                || host.GetProperty("os").GetString() != "linux-x64" || !System.Text.RegularExpressions.Regex.IsMatch(host.GetProperty("imageDigest").GetString()!, "^sha256:[a-f0-9]{64}$")) { return false; }
            var samples = host.GetProperty("metrics").EnumerateArray().Where(s => s.GetProperty("at").GetDateTimeOffset() >= start.AddSeconds(-2)).ToArray();
            if (samples.Length < 600 || samples[0].GetProperty("at").GetDateTimeOffset() > start.AddSeconds(2)
                || samples[^1].GetProperty("at").GetDateTimeOffset() < start.AddSeconds(seconds - 2)) { return false; }
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = samples[i]; var at = sample.GetProperty("at").GetDateTimeOffset();
                var cpu = sample.GetProperty("cpuSeconds").GetDouble();
                if (at > end.AddSeconds(2) || !double.IsFinite(cpu) || cpu < 0 || sample.GetProperty("residentBytes").GetInt64() <= 0 || sample.GetProperty("ioBytes").GetInt64() < 0) { return false; }
                if (i > 0)
                {
                    var interval = at - samples[i - 1].GetProperty("at").GetDateTimeOffset();
                    if (interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(5)) { return false; }
                }
            }
        }
        return e.GetProperty("postgres").GetProperty("nvme").GetBoolean() && e.GetProperty("networkGbps").GetDouble() >= 1 && e.GetProperty("apiReplicas").GetInt32() == 1 && e.GetProperty("noOtherLoad").GetBoolean();
    }

    private static bool ValidateQueryEvidence(string output)
    {
        var root = Path.Combine(output, "queries"); var summary = Path.Combine(root, "sc005-summary.json");
        var manifest = Path.Combine(output, "seed-manifest.json");
        if (!File.Exists(summary) || !File.Exists(manifest)) { return false; }
        using var evidence = JsonDocument.Parse(File.ReadAllText(summary)); using var seed = JsonDocument.Parse(File.ReadAllText(manifest));
        var report = evidence.RootElement; var expected = seed.RootElement;
        static bool Equal(JsonElement a, JsonElement b) => System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(a.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(b.GetRawText()));
        if (report.GetProperty("status").GetString() != "passed" || report.GetProperty("schemaSha256").GetString() != expected.GetProperty("schemaSha256").GetString()
            || !Equal(report.GetProperty("dataChecksums"), expected.GetProperty("tables")) || !Equal(report.GetProperty("postgresSettings"), expected.GetProperty("environment").GetProperty("postgres"))) { return false; }
        var files = report.GetProperty("planFiles").EnumerateArray().ToArray(); if (files.Length < 784) { return false; }
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(root, file.GetProperty("path").GetString()!));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)
                || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != file.GetProperty("sha256").GetString()) { return false; }
        }
        return true;
    }
}
