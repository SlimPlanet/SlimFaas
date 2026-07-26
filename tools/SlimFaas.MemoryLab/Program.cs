using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "function" => await RunFunctionAsync(Arguments.Parse(args[1..])),
        "load" => await RunLoadAsync(Arguments.Parse(args[1..])),
        "report" => RunReport(Arguments.Parse(args[1..])),
        "compare" => RunComparison(Arguments.Parse(args[1..])),
        "select-partition" => SelectPartition(Arguments.Parse(args[1..])),
        _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static async Task<int> RunFunctionAsync(Arguments arguments)
{
    int port = arguments.GetInt("port", 5050, 1, 65535);
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options =>
        options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http1));

    var app = builder.Build();
    app.MapGet("/health", () => Results.Text("OK"));
    app.MapMethods(
        "/{**path}",
        ["GET", "POST", "PUT", "PATCH", "DELETE"],
        async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });

    Console.WriteLine($"Memory lab function listening on http://127.0.0.1:{port}");
    await app.RunAsync();
    return 0;
}

static async Task<int> RunLoadAsync(Arguments arguments)
{
    string scenario = arguments.Get("scenario", "mixed").ToLowerInvariant();
    if (scenario is not ("mixed" or "sync" or "async" or "set" or "files" or "slimdata-set" or "slimdata-mixed"))
        throw new ArgumentException("scenario must be mixed, sync, async, set, files, slimdata-set, or slimdata-mixed.");

    int durationSeconds = arguments.GetInt("duration", 60, 1, 86_400);
    int concurrency = arguments.GetInt("concurrency", 12, 1, 1_024);
    int payloadBytes = arguments.GetInt("payload-bytes", 4096, 0, 1_048_576);
    int fileBytes = arguments.GetInt("file-bytes", 262_144, 1, 64 * 1024 * 1024);
    int firstPort = arguments.GetInt("first-port", 30021, 1, 65535);
    int nodeCount = arguments.GetInt("nodes", 3, 1, 32);
    int keysPerWorker = arguments.GetInt("keys-per-worker", 16, 1, 10_000);
    double targetRequestsPerSecond = arguments.GetDouble(
        "target-rps",
        defaultValue: 0d,
        minimum: 0d,
        maximum: 100_000d);
    double targetRequestsPerReplica = arguments.GetDouble(
        "target-rps-per-replica",
        defaultValue: targetRequestsPerSecond > 0d
            ? targetRequestsPerSecond / nodeCount
            : 0d,
        minimum: 0d,
        maximum: 100_000d);
    string keyPrefix = arguments.Get("key-prefix", $"memory-lab-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
    string? csvPath = arguments.GetOptional("csv");
    string? jsonPath = arguments.GetOptional("json");
    bool validate = arguments.GetBool("validate", defaultValue: false);

    var payload = CreatePayload(payloadBytes);
    var filePayload = CreatePayload(fileBytes);
    var handler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = Math.Max(32, concurrency * 2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        UseProxy = false
    };
    using var client = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    var statistics = new LoadStatistics();
    var expectedState = new ExpectedSlimDataState();
    var requestScheduler = targetRequestsPerSecond > 0d
        ? new PacedRequestScheduler(targetRequestsPerSecond)
        : null;
    using var duration = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
    using var progress = new PeriodicTimer(TimeSpan.FromSeconds(5));
    var progressTask = PrintProgressAsync(progress, statistics, duration.Token);
    var stopwatch = Stopwatch.StartNew();

    Task[] workers = Enumerable.Range(0, concurrency)
        .Select(worker => RunWorkerAsync(
            worker,
            scenario,
            nodeCount,
            firstPort,
            client,
            payload,
            filePayload,
            statistics,
            expectedState,
            keyPrefix,
            keysPerWorker,
            requestScheduler,
            duration.Token))
        .ToArray();

    await Task.WhenAll(workers);
    progress.Dispose();
    await progressTask;

    stopwatch.Stop();
    var report = statistics.CreateReport(
        scenario,
        durationSeconds,
        concurrency,
        payloadBytes,
        keysPerWorker,
        keyPrefix,
        targetRequestsPerSecond,
        targetRequestsPerReplica,
        stopwatch.Elapsed);
    PrintLoadReport(report);
    if (!string.IsNullOrWhiteSpace(csvPath))
        statistics.WriteCsv(csvPath);
    if (!string.IsNullOrWhiteSpace(jsonPath))
        WriteJson(jsonPath, report, statistics.Samples);

    var validationFailures = 0;
    if (validate && scenario == "slimdata-mixed")
    {
        validationFailures = await ValidateSlimDataStateAsync(
            client,
            expectedState,
            nodeCount,
            firstPort,
            payload,
            CancellationToken.None);
    }

    return statistics.Failed == 0 && validationFailures == 0 ? 0 : 1;
}

static async Task RunWorkerAsync(
    int worker,
    string scenario,
    int nodeCount,
    int firstPort,
    HttpClient client,
    byte[] payload,
    byte[] filePayload,
    LoadStatistics statistics,
    ExpectedSlimDataState expectedState,
    string keyPrefix,
    int keysPerWorker,
    PacedRequestScheduler? requestScheduler,
    CancellationToken cancellationToken)
{
    long sequence = 0;
    while (!cancellationToken.IsCancellationRequested)
    {
        if (requestScheduler is not null)
        {
            try
            {
                await requestScheduler.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        var operation = SelectOperation(scenario, worker, sequence);
        int node = (int)((sequence + worker) % nodeCount);
        var baseUri = new Uri($"http://127.0.0.1:{firstPort + node}");
        var startedAt = Stopwatch.GetTimestamp();
        var timestamp = DateTimeOffset.UtcNow;
        try
        {
            await ExecuteOperationAsync(
                operation,
                worker,
                node,
                nodeCount,
                firstPort,
                baseUri,
                client,
                payload,
                filePayload,
                expectedState,
                keyPrefix,
                keysPerWorker,
                scenario,
                sequence,
                CancellationToken.None);
            statistics.RecordCompleted(
                new OperationSample(
                    timestamp,
                    worker,
                    node,
                    sequence,
                    operation,
                    true,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    null));
        }
        catch (Exception exception)
        {
            statistics.RecordFailure(
                new OperationSample(
                    timestamp,
                    worker,
                    node,
                    sequence,
                    operation,
                    false,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    exception.Message),
                exception);
        }

        sequence++;
    }
}

static async Task ExecuteOperationAsync(
    Operation operation,
    int worker,
    int node,
    int nodeCount,
    int firstPort,
    Uri baseUri,
    HttpClient client,
    byte[] payload,
    byte[] filePayload,
    ExpectedSlimDataState expectedState,
    string keyPrefix,
    int keysPerWorker,
    string scenario,
    long sequence,
    CancellationToken cancellationToken)
{
    switch (operation)
    {
        case Operation.Sync:
            await PostBytesAsync(
                client,
                new Uri(baseUri, "/function/memory-function/echo"),
                payload,
                cancellationToken);
            break;

        case Operation.Async:
            await PostBytesAsync(
                client,
                new Uri(baseUri, "/async-function/memory-function/echo"),
                payload,
                cancellationToken);
            break;

        case Operation.Set:
        {
            string id = $"memory-lab-set-{worker}";
            await PostBytesAsync(
                client,
                new Uri(baseUri, $"/data/sets/{id}?ttl=120000"),
                payload,
                cancellationToken);
            await GetEventuallyAsync(
                client,
                new Uri(baseUri, $"/data/sets/{id}"),
                cancellationToken);
            break;
        }

        case Operation.SlimDataSet:
        {
            string id = BoundedId(
                keyPrefix,
                "set",
                worker,
                scenario == "slimdata-mixed" ? sequence / 16 : sequence,
                keysPerWorker);
            await PostBytesAsync(
                client,
                new Uri(baseUri, $"/data/sets/{id}"),
                payload,
                cancellationToken);
            expectedState.Sets[id] = true;
            break;
        }

        case Operation.SlimDataDelete:
        {
            string id = BoundedId(keyPrefix, "set", worker, sequence / 16, keysPerWorker);
            await DeleteAsync(client, new Uri(baseUri, $"/data/sets/{id}"), cancellationToken);
            expectedState.Sets[id] = false;
            break;
        }

        case Operation.SlimDataIncrement:
        {
            string id = BoundedId(keyPrefix, "counter", worker, sequence / 16, keysPerWorker);
            string value = await PostAndReadStringAsync(
                client,
                new Uri(baseUri, $"/data/sets/{id}/incr"),
                cancellationToken);
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual))
                throw new InvalidDataException($"Increment returned invalid integer '{value}'.");
            var expected = expectedState.Counters.AddOrUpdate(id, 1L, static (_, current) => current + 1L);
            if (actual != expected)
                throw new InvalidDataException($"Increment result for {id} is {actual}; expected {expected}.");
            break;
        }

        case Operation.SlimDataHashSet:
        {
            string id = BoundedId(keyPrefix, "hash", worker, sequence / 16, keysPerWorker);
            await PostBytesAsync(
                client,
                new Uri(baseUri, $"/data/hashsets?id={id}"),
                payload,
                cancellationToken);
            expectedState.Hashsets[id] = true;
            break;
        }

        case Operation.SlimDataHashDelete:
        {
            string id = BoundedId(keyPrefix, "hash", worker, sequence / 16, keysPerWorker);
            await DeleteAsync(client, new Uri(baseUri, $"/data/hashsets/{id}"), cancellationToken);
            expectedState.Hashsets[id] = false;
            break;
        }

        case Operation.Files:
        {
            string id = $"memory-lab-file-{worker}";
            await PostBytesAsync(
                client,
                new Uri(baseUri, $"/data/files?id={id}&ttl=120000"),
                filePayload,
                cancellationToken,
                "application/octet-stream");

            int readNode = (node + 1) % nodeCount;
            await GetEventuallyAsync(
                client,
                new Uri($"http://127.0.0.1:{firstPort + readNode}/data/files/{id}"),
                cancellationToken);
            break;
        }

        default:
            throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
    }
}

static async Task GetEventuallyAsync(
    HttpClient client,
    Uri uri,
    CancellationToken cancellationToken)
{
    const int maxAttempts = 20;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        using var response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode != HttpStatusCode.NotFound || attempt == maxAttempts)
        {
            await EnsureSuccessAndDrainAsync(response, cancellationToken);
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
    }
}

static async Task PostBytesAsync(
    HttpClient client,
    Uri uri,
    byte[] bytes,
    CancellationToken cancellationToken,
    string contentType = "application/octet-stream")
{
    using var content = new ByteArrayContent(bytes);
    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
    using var response = await client.PostAsync(uri, content, cancellationToken);
    await EnsureSuccessAndDrainAsync(response, cancellationToken);
}

static async Task DeleteAsync(
    HttpClient client,
    Uri uri,
    CancellationToken cancellationToken)
{
    using var response = await client.DeleteAsync(uri, cancellationToken);
    await EnsureSuccessAndDrainAsync(response, cancellationToken);
}

static async Task<string> PostAndReadStringAsync(
    HttpClient client,
    Uri uri,
    CancellationToken cancellationToken)
{
    using var response = await client.PostAsync(uri, content: null, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        string details = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"HTTP {(int)response.StatusCode} ({response.StatusCode}) from {response.RequestMessage?.RequestUri}: {details}");
    }
    return await response.Content.ReadAsStringAsync(cancellationToken);
}

static async Task EnsureSuccessAndDrainAsync(
    HttpResponseMessage response,
    CancellationToken cancellationToken)
{
    if (!response.IsSuccessStatusCode)
    {
        string details = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"HTTP {(int)response.StatusCode} ({response.StatusCode}) from {response.RequestMessage?.RequestUri}: {details}");
    }

    await response.Content.CopyToAsync(Stream.Null, cancellationToken);
}

static Operation SelectOperation(string scenario, int worker, long sequence)
{
    if (scenario == "slimdata-set")
        return Operation.SlimDataSet;

    if (scenario == "slimdata-mixed")
    {
        // Deterministic 16-operation cycle covering every public SlimData mutation path.
        int slimDataSlot = (int)((sequence + worker) & 15);
        return slimDataSlot switch
        {
            < 4 => Operation.SlimDataSet,
            < 6 => Operation.SlimDataDelete,
            < 9 => Operation.SlimDataIncrement,
            < 12 => Operation.SlimDataHashSet,
            < 14 => Operation.SlimDataHashDelete,
            _ => Operation.Async
        };
    }

    if (scenario != "mixed")
        return Enum.Parse<Operation>(scenario, ignoreCase: true);

    // 6 sync + 6 async + 3 set + 1 files operations per deterministic cycle.
    int slot = (int)((sequence + worker) & 15);
    return slot switch
    {
        < 6 => Operation.Sync,
        < 12 => Operation.Async,
        < 15 => Operation.Set,
        _ => Operation.Files
    };
}

static string BoundedId(
    string prefix,
    string kind,
    int worker,
    long sequence,
    int keysPerWorker)
    => $"{prefix}-{kind}-{worker}-{sequence % keysPerWorker}";

static void PrintLoadReport(LoadReport report)
{
    Console.WriteLine(
        FormattableString.Invariant(
            $"completed={report.Completed} failed={report.Failed} elapsed={report.ElapsedSeconds:F1}s rate={report.Rate:F1}/s"));
    Console.WriteLine("operation,completed,failed,rate_per_second,p50_ms,p95_ms,p99_ms,max_ms");
    foreach (var operation in report.Operations)
    {
        Console.WriteLine(
            FormattableString.Invariant(
                $"{operation.Operation},{operation.Completed},{operation.Failed},{operation.RatePerSecond:F2},{operation.P50Milliseconds:F3},{operation.P95Milliseconds:F3},{operation.P99Milliseconds:F3},{operation.MaxMilliseconds:F3}"));
    }
}

static void WriteJson(
    string path,
    LoadReport report,
    IReadOnlyList<OperationSample> samples)
{
    EnsureParentDirectory(path);
    var document = new
    {
        report,
        samples = samples.Select(static sample => new
        {
            sample.Timestamp,
            sample.Worker,
            sample.Node,
            sample.Sequence,
            operation = sample.Operation.ToString(),
            sample.Success,
            sample.LatencyMilliseconds,
            sample.Error
        })
    };
    File.WriteAllText(
        path,
        JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
}

static void EnsureParentDirectory(string path)
{
    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);
}

static async Task<int> ValidateSlimDataStateAsync(
    HttpClient client,
    ExpectedSlimDataState expected,
    int nodeCount,
    int firstPort,
    byte[] payload,
    CancellationToken cancellationToken)
{
    var failures = new List<string>();
    await WaitForQueueDrainAsync(client, nodeCount, firstPort, failures, cancellationToken);
    var validationIndex = 0;

    foreach (var (id, shouldExist) in expected.Sets.OrderBy(static item => item.Key))
    {
        var node = validationIndex++ % nodeCount;
        using var response = await client.GetAsync(
            $"http://127.0.0.1:{firstPort + node}/data/sets/{id}",
            cancellationToken);
        if (!shouldExist)
        {
            if (response.StatusCode != HttpStatusCode.NotFound)
                failures.Add($"Set {id} should be absent but returned HTTP {(int)response.StatusCode}.");
            continue;
        }

        if (!response.IsSuccessStatusCode)
        {
            failures.Add($"Set {id} should exist but returned HTTP {(int)response.StatusCode}.");
            continue;
        }
        var actual = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!actual.AsSpan().SequenceEqual(payload))
            failures.Add($"Set {id} payload differs from the last successful write.");
    }

    foreach (var (id, shouldExist) in expected.Hashsets.OrderBy(static item => item.Key))
    {
        var node = validationIndex++ % nodeCount;
        using var response = await client.GetAsync(
            $"http://127.0.0.1:{firstPort + node}/data/hashsets/{id}",
            cancellationToken);
        if (!shouldExist)
        {
            if (response.StatusCode != HttpStatusCode.NotFound)
                failures.Add($"Hashset {id} should be absent but returned HTTP {(int)response.StatusCode}.");
            continue;
        }

        if (!response.IsSuccessStatusCode)
        {
            failures.Add($"Hashset {id} should exist but returned HTTP {(int)response.StatusCode}.");
            continue;
        }
        var actual = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!actual.AsSpan().SequenceEqual(payload))
            failures.Add($"Hashset {id} payload differs from the last successful write.");
    }

    foreach (var (id, expectedValue) in expected.Counters.OrderBy(static item => item.Key))
    {
        var node = validationIndex++ % nodeCount;
        using var response = await client.GetAsync(
            $"http://127.0.0.1:{firstPort + node}/data/sets/{id}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            failures.Add($"Counter {id} returned HTTP {(int)response.StatusCode}.");
            continue;
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual) ||
            actual != expectedValue)
        {
            failures.Add($"Counter {id} is '{text}'; expected {expectedValue}.");
        }
    }

    Console.WriteLine(
        "validation sets={0} hashsets={1} counters={2} failures={3}",
        expected.Sets.Count,
        expected.Hashsets.Count,
        expected.Counters.Count,
        failures.Count);
    foreach (var failure in failures.Take(20))
        Console.Error.WriteLine($"validation failed: {failure}");
    return failures.Count;
}

static async Task WaitForQueueDrainAsync(
    HttpClient client,
    int nodeCount,
    int firstPort,
    List<string> failures,
    CancellationToken cancellationToken)
{
    const string metricName = "slimdata_state_queue_items";
    for (var attempt = 0; attempt < 120; attempt++)
    {
        var allDrained = true;
        for (var node = 0; node < nodeCount; node++)
        {
            var metrics = await client.GetStringAsync(
                $"http://127.0.0.1:{firstPort + node}/metrics",
                cancellationToken);
            var value = ReadMetric(metrics, metricName);
            allDrained &= value == 0d;
        }

        if (allDrained)
            return;
        await Task.Delay(500, cancellationToken);
    }

    failures.Add("Async queues did not drain to zero within 60 seconds.");
}

static double ReadMetric(string metrics, string metricName)
{
    foreach (var line in metrics.Split('\n'))
    {
        if (!line.StartsWith(metricName, StringComparison.Ordinal))
            continue;
        var separator = line.LastIndexOf(' ');
        if (separator > 0 &&
            double.TryParse(
                line.AsSpan(separator + 1),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return value;
        }
    }
    return double.NaN;
}

static byte[] CreatePayload(int length)
{
    var result = new byte[length];
    for (var index = 0; index < result.Length; index++)
        result[index] = (byte)('a' + index % 23);
    return result;
}

static async Task PrintProgressAsync(
    PeriodicTimer timer,
    LoadStatistics statistics,
    CancellationToken cancellationToken)
{
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            Console.WriteLine(
                "progress completed={0} failed={1}",
                statistics.Completed,
                statistics.Failed);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (ObjectDisposedException)
    {
    }
}

static int RunComparison(Arguments arguments)
{
    string root = Path.GetFullPath(arguments.GetRequired("root"));
    string output = Path.GetFullPath(arguments.GetRequired("output"));
    string baselineVariant = arguments.Get("baseline", "before");
    var runs = Directory
        .EnumerateFiles(root, "operations.json", SearchOption.AllDirectories)
        .Select(ReadBenchmarkRun)
        .OrderBy(static run => run.Scenario, StringComparer.Ordinal)
        .ThenBy(static run => run.TargetRequestsPerReplica)
        .ThenBy(static run => run.Concurrency)
        .ThenBy(static run => run.Repetition)
        .ThenBy(static run => run.Variant, StringComparer.Ordinal)
        .ToArray();
    if (runs.Length == 0)
        throw new InvalidOperationException($"No operations.json files found below '{root}'.");

    var builder = new StringBuilder();
    builder.AppendLine("# SlimData batch-mode benchmark");
    builder.AppendLine();
    builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
    builder.AppendLine($"Baseline: `{baselineVariant}`");
    builder.AppendLine();
    builder.AppendLine("## Individual runs");
    builder.AppendLine();
    builder.AppendLine("| Variant | Scenario | target/replica/s | C | Rep | ops/s | errors | p50 ms | p95 ms | p99 ms | max RSS MiB | RAFT entries | mutations | entries/mutation | WAL Δ bytes | CPU s |");
    builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var run in runs)
    {
        builder.AppendLine(FormattableString.Invariant(
            $"| {run.Variant} | {run.Scenario} | {run.TargetRequestsPerReplica:F1} | {run.Concurrency} | {run.Repetition} | {run.Rate:F2} | {run.Failed} | {run.P50Milliseconds:F2} | {run.P95Milliseconds:F2} | {run.P99Milliseconds:F2} | {run.PeakRssMegabytes:F1} | {run.RaftEntries:F0} | {run.Mutations:F0} | {Divide(run.RaftEntries, run.Mutations):F4} | {run.WalDeltaBytes:F0} | {run.CpuSeconds:F1} |"));
    }

    builder.AppendLine();
    builder.AppendLine("## Median comparison and acceptance criteria");
    builder.AppendLine();
    builder.AppendLine("| Candidate | Scenario | target/replica/s | C | base ops/s | candidate ops/s | throughput | candidate p95 | candidate p99 | p99 change | RSS change | errors | verdict |");
    builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");

    var comparisonCount = 0;
    var outcomes = new List<ComparisonOutcome>();
    foreach (var configuration in runs
                 .GroupBy(static run => (
                     run.Scenario,
                     run.TargetRequestsPerSecond,
                     run.TargetRequestsPerReplica,
                     run.Concurrency))
                 .OrderBy(static group => group.Key.Scenario, StringComparer.Ordinal)
                 .ThenBy(static group => group.Key.TargetRequestsPerReplica)
                 .ThenBy(static group => group.Key.Concurrency))
    {
        var baseline = configuration
            .Where(run => string.Equals(run.Variant, baselineVariant, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (baseline.Length == 0)
            continue;

        foreach (var candidateGroup in configuration
                     .Where(run => !string.Equals(
                         run.Variant,
                         baselineVariant,
                         StringComparison.OrdinalIgnoreCase))
                     .GroupBy(static run => run.Variant, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = candidateGroup.ToArray();
            var baselineRate = Median(baseline.Select(static run => run.Rate));
            var candidateRate = Median(candidate.Select(static run => run.Rate));
            var baselineP99 = Median(baseline.Select(static run => run.P99Milliseconds));
            var candidateP95 = Median(candidate.Select(static run => run.P95Milliseconds));
            var candidateP99 = Median(candidate.Select(static run => run.P99Milliseconds));
            var baselineRss = Median(baseline.Select(static run => run.PeakRssMegabytes));
            var candidateRss = Median(candidate.Select(static run => run.PeakRssMegabytes));
            var errors = baseline.Sum(static run => run.Failed) +
                         candidate.Sum(static run => run.Failed);
            var throughputRatio = Divide(candidateRate, baselineRate);
            var p99Ratio = Divide(candidateP99, baselineP99);
            var rssRatio = Divide(candidateRss, baselineRss);
            var isLowLoad = configuration.Key.TargetRequestsPerSecond > 0d;
            var pass = isLowLoad
                ? errors == 0 &&
                  candidateRate >= configuration.Key.TargetRequestsPerSecond * 0.90d &&
                  candidateP95 < 50d &&
                  candidateP99 < 75d
                : errors == 0 &&
                  throughputRatio >= 0.90d &&
                  p99Ratio <= 1.20d &&
                  rssRatio <= 1.15d;
            comparisonCount++;
            outcomes.Add(new ComparisonOutcome(
                candidateGroup.Key,
                isLowLoad,
                pass,
                throughputRatio));

            builder.AppendLine(FormattableString.Invariant(
                $"| {candidateGroup.Key} | {configuration.Key.Scenario} | {configuration.Key.TargetRequestsPerReplica:F1} | {configuration.Key.Concurrency} | {baselineRate:F2} | {candidateRate:F2} | {throughputRatio:P1} | {candidateP95:F2} | {candidateP99:F2} | {p99Ratio - 1:P1} | {rssRatio - 1:P1} | {errors} | {(pass ? "PASS" : "FAIL")} |"));
        }
    }

    if (comparisonCount == 0)
        throw new InvalidOperationException($"No run could be compared with baseline '{baselineVariant}'.");

    builder.AppendLine();
    builder.AppendLine("## Adoption decision");
    builder.AppendLine();
    builder.AppendLine("| Candidate | low-load SLO | high-load safety | median high-load gain | decision |");
    builder.AppendLine("|---|---|---|---:|---|");
    var adoptionPass = false;
    foreach (var candidate in outcomes
                 .GroupBy(static outcome => outcome.Candidate, StringComparer.OrdinalIgnoreCase)
                 .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
    {
        var lowLoad = candidate.Where(static outcome => outcome.IsLowLoad).ToArray();
        var highLoad = candidate.Where(static outcome => !outcome.IsLowLoad).ToArray();
        var lowLoadPass = lowLoad.Length > 0 && lowLoad.All(static outcome => outcome.Pass);
        var highLoadPass = highLoad.Length > 0 && highLoad.All(static outcome => outcome.Pass);
        var medianHighLoadGain = Median(
            highLoad.Select(static outcome => outcome.ThroughputRatio - 1d));
        var isPartitioned = candidate.Key.Contains(
            "partitioned",
            StringComparison.OrdinalIgnoreCase);
        var adopt = lowLoadPass &&
                    highLoadPass &&
                    (!isPartitioned || medianHighLoadGain >= 0.10d);
        adoptionPass |= adopt;
        var decision = adopt
            ? "ADOPT"
            : isPartitioned && lowLoadPass && highLoadPass
                ? "KEEP OPT-IN — gain < 10%"
                : "DO NOT ADOPT";
        builder.AppendLine(FormattableString.Invariant(
            $"| {candidate.Key} | {(lowLoadPass ? "PASS" : "FAIL")} | {(highLoadPass ? "PASS" : "FAIL")} | {medianHighLoadGain:P1} | {decision} |"));
    }

    builder.AppendLine();
    builder.AppendLine($"Overall adoption result: **{(adoptionPass ? "PASS" : "FAIL")}**.");
    builder.AppendLine();
    builder.AppendLine("Low-load runs require at least 90% of the target rate, p95 < 50 ms and p99 < 75 ms. Unpaced runs require throughput >= 90% of baseline, p99 <= 120% and RSS <= 115%. The overall adoption result passes when at least one candidate is adoptable. A partitioned candidate is recommended only when its median high-load throughput gain is at least 10%.");
    builder.AppendLine();
    builder.AppendLine("RAFT entries are derived from committed log-index deltas. Mutation counts use the unified-batch metric when available; WAL deltas are medians across the three replicas.");

    EnsureParentDirectory(output);
    File.WriteAllText(output, builder.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"Comparison report: {output}");
    return adoptionPass ? 0 : 1;
}

static BenchmarkRun ReadBenchmarkRun(string jsonPath)
{
    var runDirectory = Path.GetDirectoryName(jsonPath)
        ?? throw new InvalidOperationException($"No run directory for '{jsonPath}'.");
    var manifest = ReadManifest(Path.Combine(runDirectory, "manifest.txt"));
    var label = manifest.GetValueOrDefault("run_label", Path.GetFileName(runDirectory));
    var repetition = 0;
    var repetitionMarker = label.LastIndexOf("-r", StringComparison.OrdinalIgnoreCase);
    if (repetitionMarker >= 0)
    {
        _ = int.TryParse(
            label.AsSpan(repetitionMarker + 2),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out repetition);
    }
    var baseLabel = repetitionMarker > 0 ? label[..repetitionMarker] : label;
    var variant = baseLabel.StartsWith("before", StringComparison.OrdinalIgnoreCase)
        ? "before"
        : baseLabel.StartsWith("after", StringComparison.OrdinalIgnoreCase)
            ? "after"
            : baseLabel;

    using var document = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
    var report = document.RootElement.GetProperty("report");
    var scenario = report.GetProperty("Scenario").GetString() ?? "unknown";
    var concurrency = report.GetProperty("Concurrency").GetInt32();
    var targetRequestsPerSecond = report.TryGetProperty(
        "TargetRequestsPerSecond",
        out var targetProperty)
        ? targetProperty.GetDouble()
        : double.TryParse(
            manifest.GetValueOrDefault("target_requests_per_second", "0"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var manifestTarget)
            ? manifestTarget
            : 0d;
    var targetRequestsPerReplica = report.TryGetProperty(
        "TargetRequestsPerReplica",
        out var targetPerReplicaProperty)
        ? targetPerReplicaProperty.GetDouble()
        : double.TryParse(
            manifest.GetValueOrDefault("target_requests_per_replica", "0"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var manifestTargetPerReplica)
            ? manifestTargetPerReplica
            : targetRequestsPerSecond / 3d;
    var rate = report.GetProperty("Rate").GetDouble();
    var failed = report.GetProperty("Failed").GetInt64();
    var all = report.GetProperty("Operations")
        .EnumerateArray()
        .First(operation => string.Equals(
            operation.GetProperty("Operation").GetString(),
            "All",
            StringComparison.Ordinal));
    var inferredMutations = report.GetProperty("Completed").GetDouble();
    if (scenario == "slimdata-mixed")
    {
        var asyncCompleted = report.GetProperty("Operations")
            .EnumerateArray()
            .Where(operation => string.Equals(
                operation.GetProperty("Operation").GetString(),
                nameof(Operation.Async),
                StringComparison.Ordinal))
            .Select(operation => operation.GetProperty("Completed").GetDouble())
            .FirstOrDefault();
        inferredMutations += asyncCompleted * 2d;
    }

    var actualMutations = SumMetricDelta(
        runDirectory,
        "slimdata_command_batch_operations_total");
    var raftEntries = MedianMetricDelta(
        runDirectory,
        "slimdata_raft_last_log_index");
    var cpuSeconds = SumMetricDelta(
        runDirectory,
        "slimdata_process_cpu_seconds_total");
    var mutations = actualMutations > 0d ? actualMutations : inferredMutations;

    return new BenchmarkRun(
        variant,
        scenario,
        concurrency,
        targetRequestsPerSecond,
        targetRequestsPerReplica,
        repetition,
        rate,
        failed,
        all.GetProperty("P50Milliseconds").GetDouble(),
        all.GetProperty("P95Milliseconds").GetDouble(),
        all.GetProperty("P99Milliseconds").GetDouble(),
        ReadPeakRssMegabytes(runDirectory),
        raftEntries,
        mutations,
        ReadWalDeltaBytes(runDirectory),
        cpuSeconds);
}

static int SelectPartition(Arguments arguments)
{
    var root = Path.GetFullPath(arguments.GetRequired("root"));
    var output = Path.GetFullPath(arguments.GetRequired("output"));
    var baselineVariant = arguments.Get("baseline", "global-low-latency");
    var runs = Directory
        .EnumerateFiles(root, "operations.json", SearchOption.AllDirectories)
        .Select(ReadBenchmarkRun)
        .Where(static run => run.TargetRequestsPerSecond <= 0d)
        .ToArray();
    var candidates = new List<PartitionSelection>();

    foreach (var candidateRuns in runs
                 .Where(static run => run.Variant.StartsWith(
                     "partitioned",
                     StringComparison.OrdinalIgnoreCase))
                 .GroupBy(static run => run.Variant, StringComparer.OrdinalIgnoreCase))
    {
        var outcomes = new List<ComparisonOutcome>();
        foreach (var configuration in runs
                     .GroupBy(static run => (run.Scenario, run.Concurrency)))
        {
            var baseline = configuration
                .Where(run => string.Equals(
                    run.Variant,
                    baselineVariant,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var candidate = configuration
                .Where(run => string.Equals(
                    run.Variant,
                    candidateRuns.Key,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (baseline.Length == 0 || candidate.Length == 0)
                continue;

            var throughputRatio = Divide(
                Median(candidate.Select(static run => run.Rate)),
                Median(baseline.Select(static run => run.Rate)));
            var p99Ratio = Divide(
                Median(candidate.Select(static run => run.P99Milliseconds)),
                Median(baseline.Select(static run => run.P99Milliseconds)));
            var rssRatio = Divide(
                Median(candidate.Select(static run => run.PeakRssMegabytes)),
                Median(baseline.Select(static run => run.PeakRssMegabytes)));
            var errors = baseline.Sum(static run => run.Failed) +
                         candidate.Sum(static run => run.Failed);
            outcomes.Add(new ComparisonOutcome(
                candidateRuns.Key,
                IsLowLoad: false,
                Pass: errors == 0 &&
                      throughputRatio >= 0.90d &&
                      p99Ratio <= 1.20d &&
                      rssRatio <= 1.15d,
                throughputRatio));
        }

        if (outcomes.Count == 0)
            continue;
        candidates.Add(new PartitionSelection(
            candidateRuns.Key,
            outcomes.All(static outcome => outcome.Pass),
            Median(outcomes.Select(static outcome => outcome.ThroughputRatio - 1d))));
    }

    var selected = candidates
        .OrderByDescending(static candidate => candidate.Safe)
        .ThenByDescending(static candidate => candidate.MedianThroughputGain)
        .FirstOrDefault();
    if (selected is null)
        throw new InvalidOperationException(
            $"No partitioned candidate could be compared with '{baselineVariant}'.");

    var partitionMarker = selected.Variant.LastIndexOf("-p", StringComparison.OrdinalIgnoreCase);
    if (partitionMarker < 0 ||
        !int.TryParse(
            selected.Variant.AsSpan(partitionMarker + 2),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var partitions))
    {
        throw new InvalidDataException(
            $"Cannot read the partition count from variant '{selected.Variant}'.");
    }

    EnsureParentDirectory(output);
    File.WriteAllText(
        output,
        FormattableString.Invariant(
            $"selected_variant={selected.Variant}\nselected_partitions={partitions}\nscreening_safe={(selected.Safe ? "true" : "false")}\nscreening_median_throughput_gain={selected.MedianThroughputGain:F6}\nscreening_adoption_eligible={(selected.Safe && selected.MedianThroughputGain >= 0.10d ? "true" : "false")}\n"),
        new UTF8Encoding(false));
    Console.WriteLine(
        FormattableString.Invariant(
            $"Selected {selected.Variant}: median throughput gain {selected.MedianThroughputGain:P1}."));
    return 0;
}

static Dictionary<string, string> ReadManifest(string path)
    => File.Exists(path)
        ? File.ReadLines(path)
            .Select(static line => line.Split('=', 2))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.Ordinal)
        : new Dictionary<string, string>(StringComparer.Ordinal);

static double ReadPeakRssMegabytes(string runDirectory)
{
    var path = Path.Combine(runDirectory, "memory.csv");
    if (!File.Exists(path))
        return 0d;
    var samples = File.ReadLines(path)
        .Skip(1)
        .Select(MemorySample.Parse)
        .Where(static sample => sample.Phase == SamplePhase.Load)
        .ToArray();
    if (samples.Length == 0)
        return 0d;

    var manifest = ReadManifest(Path.Combine(runDirectory, "manifest.txt"));
    var durationSeconds = double.TryParse(
        manifest.GetValueOrDefault("duration_seconds", "0"),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out var parsedDuration)
        ? parsedDuration
        : 0d;
    var cutoff = durationSeconds > 0d
        ? samples.Min(static sample => sample.Timestamp)
            .AddSeconds(durationSeconds + 2d)
        : DateTimeOffset.MaxValue;
    return samples
        .Where(sample => sample.Timestamp <= cutoff)
        .Select(static sample => sample.RssMegabytes)
        .DefaultIfEmpty()
        .Max();
}

static double SumMetricDelta(string runDirectory, string metricName)
{
    double result = 0d;
    for (var node = 0; node < 3; node++)
    {
        var before = ReadMetricFile(
            Path.Combine(runDirectory, $"metrics-before-slimfaas-{node}.prom"),
            metricName);
        var after = ReadMetricFile(
            Path.Combine(runDirectory, $"metrics-after-slimfaas-{node}.prom"),
            metricName);
        if (!double.IsNaN(before) && !double.IsNaN(after))
            result += Math.Max(0d, after - before);
    }
    return result;
}

static double MedianMetricDelta(string runDirectory, string metricName)
{
    var deltas = new List<double>(3);
    for (var node = 0; node < 3; node++)
    {
        var before = ReadMetricFile(
            Path.Combine(runDirectory, $"metrics-before-slimfaas-{node}.prom"),
            metricName);
        var after = ReadMetricFile(
            Path.Combine(runDirectory, $"metrics-after-slimfaas-{node}.prom"),
            metricName);
        if (!double.IsNaN(before) && !double.IsNaN(after))
            deltas.Add(Math.Max(0d, after - before));
    }
    return Median(deltas);
}

static double ReadMetricFile(string path, string metricName)
    => File.Exists(path)
        ? ReadMetric(File.ReadAllText(path), metricName)
        : double.NaN;

static double ReadWalDeltaBytes(string runDirectory)
{
    var deltas = new List<double>(3);
    for (var node = 0; node < 3; node++)
    {
        var before = SumWalFiles(
            Path.Combine(runDirectory, $"wal-files-before-slimfaas-{node}.txt"));
        var after = SumWalFiles(
            Path.Combine(runDirectory, $"wal-files-after-slimfaas-{node}.txt"));
        if (before >= 0d && after >= 0d)
            deltas.Add(after - before);
    }
    return Median(deltas);
}

static double SumWalFiles(string path)
{
    if (!File.Exists(path))
        return -1d;
    double result = 0d;
    foreach (var line in File.ReadLines(path))
    {
        var separator = line.IndexOf(' ');
        if (separator > 0 &&
            double.TryParse(
                line.AsSpan(0, separator),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var bytes))
        {
            result += bytes;
        }
    }
    return result;
}

static double Divide(double numerator, double denominator)
    => denominator == 0d ? 0d : numerator / denominator;

static int RunReport(Arguments arguments)
{
    string csvPath = arguments.GetRequired("csv");
    var samples = File.ReadLines(csvPath)
        .Skip(1)
        .Select(MemorySample.Parse)
        .GroupBy(sample => sample.Node, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToArray();

    if (samples.Length == 0)
        throw new InvalidOperationException($"No memory samples found in '{csvPath}'.");

    Console.WriteLine("node,load_samples,start_mb,end_mb,peak_mb,slope_mb_per_min,cooldown_end_mb");
    foreach (var node in samples)
    {
        var ordered = node.OrderBy(sample => sample.Timestamp).ToArray();
        var load = ordered.Where(sample => sample.Phase == SamplePhase.Load).ToArray();
        // Native AOT server-GC heaps can take several collection cycles to reach
        // their reusable steady-state capacity. Fit the tail half so startup and
        // heap expansion are not misreported as retained-memory growth.
        var measured = load.Skip(load.Length / 2).ToArray();
        if (measured.Length == 0)
            measured = ordered.Skip(ordered.Length / 2).ToArray();

        double start = Median(measured.Take(Math.Min(3, measured.Length)).Select(sample => sample.RssMegabytes));
        double end = Median(measured.TakeLast(Math.Min(3, measured.Length)).Select(sample => sample.RssMegabytes));
        double peak = measured.Max(sample => sample.RssMegabytes);
        double slope = LinearRegressionSlope(measured) * 60;
        double cooldownEnd = Median(
            ordered.TakeLast(Math.Min(3, ordered.Length)).Select(sample => sample.RssMegabytes));
        Console.WriteLine(FormattableString.Invariant(
            $"{node.Key},{measured.Length},{start:F2},{end:F2},{peak:F2},{slope:F3},{cooldownEnd:F2}"));
    }

    return 0;
}

static double Median(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    if (ordered.Length == 0)
        return 0;
    int middle = ordered.Length / 2;
    return ordered.Length % 2 == 0
        ? (ordered[middle - 1] + ordered[middle]) / 2
        : ordered[middle];
}

static double LinearRegressionSlope(IReadOnlyList<MemorySample> samples)
{
    if (samples.Count < 2)
        return 0;

    var origin = samples[0].Timestamp;
    double averageX = samples.Average(sample => (sample.Timestamp - origin).TotalSeconds);
    double averageY = samples.Average(sample => sample.RssMegabytes);
    double numerator = 0;
    double denominator = 0;
    foreach (var sample in samples)
    {
        double x = (sample.Timestamp - origin).TotalSeconds - averageX;
        double y = sample.RssMegabytes - averageY;
        numerator += x * y;
        denominator += x * x;
    }

    return denominator == 0 ? 0 : numerator / denominator;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        SlimFaas.MemoryLab:
          function [--port 5050]
          load [--scenario mixed|sync|async|set|files|slimdata-set|slimdata-mixed] [--duration 60]
               [--concurrency 12] [--payload-bytes 4096] [--file-bytes 262144]
               [--first-port 30021] [--nodes 3] [--keys-per-worker 16]
               [--target-rps 0]
               [--target-rps-per-replica 0]
               [--key-prefix memory-lab] [--validate true]
               [--csv operations.csv] [--json operations.json]
          report --csv <memory.csv>
          compare --root <runs-directory> --output <comparison.md> [--baseline before]
          select-partition --root <screening-runs> --output <selection.env>
                           [--baseline global-low-latency]
        """);
}

enum Operation
{
    Sync,
    Async,
    Set,
    Files,
    SlimDataSet,
    SlimDataDelete,
    SlimDataIncrement,
    SlimDataHashSet,
    SlimDataHashDelete
}

sealed class LoadStatistics
{
    private readonly long[] _completed = new long[Enum.GetValues<Operation>().Length];
    private readonly ConcurrentQueue<OperationSample> _samples = new();
    private long _failed;
    private int _reportedFailures;

    public long Completed
    {
        get
        {
            long result = 0;
            for (var index = 0; index < _completed.Length; index++)
                result += Volatile.Read(ref _completed[index]);
            return result;
        }
    }

    public long Failed => Volatile.Read(ref _failed);

    public IReadOnlyList<OperationSample> Samples => _samples.ToArray();

    public void RecordCompleted(OperationSample sample)
    {
        Interlocked.Increment(ref _completed[(int)sample.Operation]);
        _samples.Enqueue(sample);
    }

    public void RecordFailure(OperationSample sample, Exception exception)
    {
        Interlocked.Increment(ref _failed);
        _samples.Enqueue(sample);
        if (Interlocked.Increment(ref _reportedFailures) <= 10)
            Console.Error.WriteLine($"{sample.Operation} failed: {exception.Message}");
    }

    public LoadReport CreateReport(
        string scenario,
        int durationSeconds,
        int concurrency,
        int payloadBytes,
        int keysPerWorker,
        string keyPrefix,
        double targetRequestsPerSecond,
        double targetRequestsPerReplica,
        TimeSpan elapsed)
    {
        var samples = _samples.ToArray();
        var perOperation = samples
            .GroupBy(static sample => sample.Operation)
            .OrderBy(static group => group.Key)
            .Select(group => CreateOperationReport(group.Key.ToString(), group, elapsed))
            .ToArray();
        var operations = new[]
        {
            CreateOperationReport("All", samples, elapsed)
        }.Concat(perOperation).ToArray();
        return new LoadReport(
            scenario,
            durationSeconds,
            concurrency,
            payloadBytes,
            keysPerWorker,
            keyPrefix,
            targetRequestsPerSecond,
            targetRequestsPerReplica,
            elapsed.TotalSeconds,
            Completed,
            Failed,
            Completed / Math.Max(0.001, elapsed.TotalSeconds),
            operations);
    }

    public void WriteCsv(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        writer.WriteLine("timestamp,worker,node,sequence,operation,success,latency_ms,error");
        foreach (var sample in _samples)
        {
            writer.Write(sample.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(sample.Worker.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(sample.Node.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(sample.Sequence.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(sample.Operation);
            writer.Write(',');
            writer.Write(sample.Success ? "true" : "false");
            writer.Write(',');
            writer.Write(sample.LatencyMilliseconds.ToString("F6", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(EscapeCsv(sample.Error));
        }
    }

    private static OperationReport CreateOperationReport(
        string operation,
        IEnumerable<OperationSample> operationSamples,
        TimeSpan elapsed)
    {
        var samples = operationSamples.ToArray();
        var successfulLatencies = samples
            .Where(static sample => sample.Success)
            .Select(static sample => sample.LatencyMilliseconds)
            .Order()
            .ToArray();
        var completed = successfulLatencies.LongLength;
        var failed = samples.LongLength - completed;
        return new OperationReport(
            operation,
            completed,
            failed,
            completed / Math.Max(0.001, elapsed.TotalSeconds),
            Percentile(successfulLatencies, 0.50),
            Percentile(successfulLatencies, 0.95),
            Percentile(successfulLatencies, 0.99),
            successfulLatencies.Length == 0 ? 0d : successfulLatencies[^1]);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0d;
        var rank = percentile * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return sorted[lower];
        var fraction = rank - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}

sealed class ExpectedSlimDataState
{
    internal ConcurrentDictionary<string, bool> Sets { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, bool> Hashsets { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);
}

readonly record struct OperationSample(
    DateTimeOffset Timestamp,
    int Worker,
    int Node,
    long Sequence,
    Operation Operation,
    bool Success,
    double LatencyMilliseconds,
    string? Error);

sealed record OperationReport(
    string Operation,
    long Completed,
    long Failed,
    double RatePerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds);

sealed record LoadReport(
    string Scenario,
    int DurationSeconds,
    int Concurrency,
    int PayloadBytes,
    int KeysPerWorker,
    string KeyPrefix,
    double TargetRequestsPerSecond,
    double TargetRequestsPerReplica,
    double ElapsedSeconds,
    long Completed,
    long Failed,
    double Rate,
    IReadOnlyList<OperationReport> Operations);

sealed record BenchmarkRun(
    string Variant,
    string Scenario,
    int Concurrency,
    double TargetRequestsPerSecond,
    double TargetRequestsPerReplica,
    int Repetition,
    double Rate,
    long Failed,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double PeakRssMegabytes,
    double RaftEntries,
    double Mutations,
    double WalDeltaBytes,
    double CpuSeconds);

readonly record struct ComparisonOutcome(
    string Candidate,
    bool IsLowLoad,
    bool Pass,
    double ThroughputRatio);

sealed record PartitionSelection(
    string Variant,
    bool Safe,
    double MedianThroughputGain);

sealed class Arguments
{
    private readonly IReadOnlyDictionary<string, string> _values;

    private Arguments(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            string key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"Expected --name value, found '{key}'.");
            values[key[2..]] = args[index + 1];
        }

        return new Arguments(values);
    }

    public string Get(string name, string defaultValue)
        => _values.GetValueOrDefault(name, defaultValue);

    public string GetRequired(string name)
        => _values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument --{name}.");

    public string? GetOptional(string name)
        => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public bool GetBool(string name, bool defaultValue)
    {
        var value = Get(name, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!bool.TryParse(value, out var parsed))
            throw new ArgumentException($"--{name} must be true or false.");
        return parsed;
    }

    public int GetInt(string name, int defaultValue, int minimum, int maximum)
    {
        string value = Get(name, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new ArgumentException(
                $"--{name} must be an integer between {minimum} and {maximum}.");
        }

        return parsed;
    }

    public double GetDouble(string name, double defaultValue, double minimum, double maximum)
    {
        string value = Get(name, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new ArgumentException(
                $"--{name} must be a number between {minimum} and {maximum}.");
        }

        return parsed;
    }
}

sealed class PacedRequestScheduler
{
    private readonly long _intervalTimestampTicks;
    private long _nextTimestamp;

    public PacedRequestScheduler(double requestsPerSecond)
    {
        _intervalTimestampTicks = Math.Max(
            1L,
            (long)Math.Round(
                Stopwatch.Frequency / requestsPerSecond,
                MidpointRounding.AwayFromZero));
        _nextTimestamp = Stopwatch.GetTimestamp();
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        long scheduledAt;
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var current = Volatile.Read(ref _nextTimestamp);
            scheduledAt = Math.Max(now, current);
            var next = scheduledAt + _intervalTimestampTicks;
            if (Interlocked.CompareExchange(ref _nextTimestamp, next, current) == current)
                break;
        }

        var remainingTicks = scheduledAt - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
            return;

        var delay = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
        await Task.Delay(delay, cancellationToken);
    }
}

readonly record struct MemorySample(
    DateTimeOffset Timestamp,
    string Node,
    int ProcessId,
    long RssKilobytes,
    long VirtualKilobytes,
    SamplePhase Phase)
{
    public double RssMegabytes => RssKilobytes / 1024d;

    public static MemorySample Parse(string line)
    {
        string[] columns = line.Split(',');
        if (columns.Length is not (5 or 6))
            throw new FormatException($"Invalid memory CSV line: '{line}'.");
        return new MemorySample(
            DateTimeOffset.Parse(columns[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            columns[1],
            int.Parse(columns[2], CultureInfo.InvariantCulture),
            long.Parse(columns[3], CultureInfo.InvariantCulture),
            long.Parse(columns[4], CultureInfo.InvariantCulture),
            columns.Length == 6 && string.Equals(columns[5], "cooldown", StringComparison.Ordinal)
                ? SamplePhase.Cooldown
                : SamplePhase.Load);
    }
}

enum SamplePhase
{
    Load,
    Cooldown
}
