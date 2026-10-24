namespace BlogEngine.Performance;

internal static class BenchmarkProgram
{
    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || args[i] is not ("--mode" or "--seed" or "--output" or "--diagnostic") || !values.TryAdd(args[i], args[i + 1]))
                { throw new ArgumentException("Use --mode Seed|Run --seed 20261024 --output DIRECTORY [--diagnostic true]."); }
            }
            if (values.GetValueOrDefault("--seed") != "20261024" || values.GetValueOrDefault("--mode") is not ("Seed" or "Run"))
            { throw new ArgumentException("Only the normative sc005-v1 seed 20261024 and Seed|Run modes are supported."); }
            if (values.TryGetValue("--diagnostic", out var diagnosticValue) && diagnosticValue is not ("true" or "false"))
            { throw new ArgumentException("--diagnostic must be true or false."); }
            string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new ArgumentException("Missing setting " + name);
            var output = Path.GetFullPath(values.GetValueOrDefault("--output") ?? "artifacts/performance/sc-005");
            var connection = Required("BLOG_BENCHMARK_SEED_CONNECTION"); var disposable = Required("BLOG_BENCHMARK_DISPOSABLE_DATABASE");
            var issuer = Required("BLOG_BENCHMARK_FIXTURE_ISSUER");
            var dataset = new SeedDataset(issuer);
            var image = Required("BLOG_BENCHMARK_POSTGRES_IMAGE");
            await dataset.SeedAsync(connection, disposable, output, shutdown.Token, image);
            if (values["--mode"] == "Seed") { Console.WriteLine("Seed and canonical checksums verified."); return 0; }
            var plan = new RequestPlan(dataset);
            await plan.SaveAsync(output, shutdown.Token);
            await using var identity = new BenchmarkIdentityFixture(issuer, Required("BLOG_BENCHMARK_FIXTURE_AUDIENCE"));
            await identity.StartAsync(Required("BLOG_BENCHMARK_FIXTURE_LISTEN_URL"), new Uri(Required("BLOG_BENCHMARK_FIXTURE_JWKS_URL")), shutdown.Token);
            var benchmark = new ReadBenchmark(plan, identity, new Uri(Required("BLOG_BENCHMARK_API_URL")));
            var passed = await benchmark.RunAsync(output, values.GetValueOrDefault("--diagnostic") == "true", shutdown.Token);
            await dataset.SeedAsync(connection, disposable, output, shutdown.Token, image);
            var reportPath = Path.Combine(output, "read-report.json");
            var report = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(reportPath, shutdown.Token))!;
            report["datasetUnchanged"] = true; report["status"] = passed ? "passed" : "blocked_or_failed";
            await File.WriteAllTextAsync(reportPath, report.ToJsonString(BenchmarkManifest.Json), shutdown.Token);
            Console.WriteLine(passed ? "SC-005 passed." : "SC-005 is not accepted. Inspect read-report.json and stand-evidence.json.");
            return passed ? 0 : 2;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { Console.Error.WriteLine("Benchmark cancelled."); return 130; }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        catch (Exception exception)
        {
            // Provider messages may contain connection strings, endpoints or request data.
            Console.Error.WriteLine("Benchmark failed: " + exception.GetType().Name + ". No acceptance result is claimed."); return 1;
        }
    }
}
