using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SlimFaas.Benchmarking;

namespace SlimFaasBenchmark;

// Bounded workloads only. Environment setup and independent receivers belong to
// the platform controller; no process startup is included in the measured window.
internal static class FeatureRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(CommandArguments arguments)
    {
        string scenario = arguments.Get("scenario", "streams");
        if (scenario is not ("streams" or "jobs" or "ws-sync" or "ws-async" or "ws-events"))
            throw new ArgumentException("--scenario must be streams, jobs, ws-sync, ws-async or ws-events.");
        IReadOnlyList<Uri> nodes = arguments.GetUriList("node-urls", "http://127.0.0.1:31021");
        string[] functions = arguments.Get("functions", "memory-function").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (functions.Length == 0 || functions.Distinct(StringComparer.Ordinal).Count() != functions.Length)
            throw new ArgumentException("--functions requires distinct receiver names.");
        int count = arguments.GetInt("messages", 200, 1, 10000);
        int concurrency = arguments.GetInt("concurrency", 1, 1, 1024);
        int bytes = arguments.GetInt("payload-bytes", 4096, 1, 64 * 1024 * 1024);
        int repetition = arguments.GetInt("repetition", 1, 1, 1000);
        string output = Path.GetFullPath(arguments.Get("output", "artifacts/features"));
        string receipts = arguments.GetOptional("receipts") ?? throw new ArgumentException("--receipts is required.");
        string job = arguments.Get("job", "chaos-job");
        string eventName = arguments.Get("event", "benchmark-event");
        string run = "feature-" + Guid.NewGuid().ToString("N");
        string stream = run + "-stream";
        Directory.CreateDirectory(output);
        if (File.Exists(Path.Combine(output, "feature-results.json")))
            throw new ArgumentException("Output already contains a result; retain it and use a fresh directory.");
        using var transport = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = Math.Max(32, concurrency * 2) };
        using var journal = RequestJournal.Open(Path.Combine(output, "requests"));
        using HttpMessageHandler handler = journal!.Wrap(transport);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        using var metricsClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        byte[] payload = new byte[bytes];
        new Random(42).NextBytes(payload);
        string checksum = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (scenario == "streams")
        {
            using var empty = new ByteArrayContent([]);
            using var seed = await client.PostAsync(new Uri(nodes[0], $"publish-stream/{stream}/echo"), empty);
            seed.EnsureSuccessStatusCode();
            foreach (string function in functions)
            {
                using var subscribe = await client.PostAsync(new Uri(nodes[0], $"data/streams/{stream}/functions/{Uri.EscapeDataString(function)}"), null);
                subscribe.EnsureSuccessStatusCode();
            }
            using var prepareDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!await IsDrainedAsync(client, nodes, scenario, stream, functions, job, [], prepareDeadline.Token))
                await Task.Delay(50, prepareDeadline.Token);
        }
        var samples = new ConcurrentQueue<FeatureSample>();
        await using var monitor = await ClusterResourceMonitor.StartAsync(metricsClient, nodes, diagnostics: false);
        long started = Stopwatch.GetTimestamp();
        double startedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (index, token) =>
        {
            string id = $"{run}-{index}";
            string route = scenario switch
            {
                "jobs" => $"job/{job}",
                "streams" => $"publish-stream/{stream}/echo",
                "ws-events" => $"publish-event/{eventName}",
                "ws-sync" => $"function/{functions[0]}/echo",
                _ => $"async-function/{functions[0]}/echo"
            };
            long requestStart = Stopwatch.GetTimestamp();
            double utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            double? firstByte = null;
            string? queueId = null;
            string? error = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(nodes[index % nodes.Count], route));
                request.Content = scenario == "jobs" ? CreateJobContent(id) : new ByteArrayContent(payload);
                request.Headers.Add("X-Chaos-Id", id);
                request.Headers.Add("X-Chaos-Sha256", checksum);
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (scenario is "jobs" or "streams" or "ws-async" && response.StatusCode != HttpStatusCode.Accepted)
                    throw new InvalidDataException("Expected HTTP 202 acceptance.");
                if (scenario == "ws-sync")
                {
                    await using Stream body = await response.Content.ReadAsStreamAsync(token);
                    byte[] first = new byte[1];
                    int read = await body.ReadAsync(first, token);
                    firstByte = Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    hash.AppendData(first.AsSpan(0, read));
                    byte[] buffer = new byte[65536];
                    long length = read;
                    while ((read = await body.ReadAsync(buffer, token)) != 0)
                    { hash.AppendData(buffer.AsSpan(0, read)); length += read; }
                    if (length != bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != checksum)
                        throw new InvalidDataException("Streaming response length or SHA-256 mismatch.");
                }
                else if (scenario is "jobs" or "streams")
                {
                    using JsonDocument receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    queueId = receipt.RootElement.GetProperty(scenario == "jobs" ? "Id" : "MessageId").GetString();
                }
                else await response.Content.CopyToAsync(Stream.Null, token);
                journal.RecordOperation(id, "operation-completed");
            }
            catch (Exception failure) when (failure is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
            { error = failure.Message; journal.RecordOperation(id, "operation-failed", error); }
            samples.Enqueue(new FeatureSample(id, utc, Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds, firstByte, queueId, error));
        });
        double loadSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        AsyncResourceUsage? resources = await monitor.CompleteAsync(samples.Count(sample => sample.Error is null));
        await using var drainMonitor = await ClusterResourceMonitor.StartAsync(metricsClient, nodes, diagnostics: false);
        FeatureSample[] successful = samples.Where(sample => sample.Error is null).ToArray();
        int fanout = scenario is "streams" or "ws-events" ? functions.Length : 1;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(arguments.GetInt("drain-timeout", 300, 1, 7200)));
        FeatureReceipts observed = new([], [], [], [], []);
        bool drained = scenario == "ws-sync";
        string? drainError = null;
        try
        {
            while (true)
            {
                observed = ReadReceipts(receipts, successful, scenario, functions, bytes, checksum);
                drained = observed.Missing.Count == 0 && await IsDrainedAsync(client, nodes, scenario, stream, functions, job, successful, deadline.Token);
                if (drained)
                {
                    if (scenario == "ws-async")
                        await WaitForWebSocketQueueToDrainAsync(metricsClient, nodes, deadline.Token);
                    break;
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            drainError = "Drain deadline exceeded";
            drained = false;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or TimeoutException)
        { drainError = error.Message; drained = false; }
        double completionSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        AsyncResourceUsage? drainResources = await drainMonitor.CompleteAsync(successful.LongLength);
        long failures = samples.Count(sample => sample.Error is not null) + observed.Missing.Count + observed.Duplicates.Count + observed.Corrupt.Count + (drained ? 0 : 1);
        double[] latencies = successful.Select(sample => sample.Milliseconds).Order().ToArray();
        string unit = scenario == "jobs" ? "accepted job/s" : scenario == "ws-sync" ? "request/s" : "accepted message/s";
        var row = new CampaignRow("feature-results.json", arguments.Get("label", scenario), unit, "load", scenario == "jobs" ? null : bytes, concurrency, repetition,
            successful.Length / loadSeconds, observed.Delivered.Count / completionSeconds, observed.Completed.Count / completionSeconds,
            Statistics.Percentile(latencies, .50), Statistics.Percentile(latencies, .95), Statistics.Percentile(latencies, .99),
            resources?.CpuMillisecondsPerMessage, resources?.PeakWorkingSetBytes / 1048576d, failures, failures == 0,
            scenario == "ws-sync" ? successful.Length * (double)bytes / loadSeconds / 1048576d : null,
            failures > 0 ? "FAIL" : resources is null ? "INCONCLUSIVE" : "OBSERVED", loadSeconds);
        var result = new { SchemaVersion = 1, Rows = new[] { row }, Resources = resources, DrainResources = drainResources, Samples = samples,
            Receipts = observed, StartedUtc = startedUtc, CompletionWindowSeconds = completionSeconds,
            DrainSeconds = Math.Max(0, completionSeconds - loadSeconds), DrainError = drainError,
            ExpectedDeliveries = successful.Length * fanout, ReceiverFunctions = functions,
            TtfbMilliseconds = successful.Select(sample => sample.FirstByteMilliseconds).ToArray(),
            JobQueueWaitMilliseconds = scenario != "jobs" ? null : observed.Delivered.Select(receipt => (receipt.Utc - successful.Single(sample => sample.Id == receipt.Id).StartedUtc) * 1000).ToArray(),
            Integrity = failures == 0 };
        await File.WriteAllTextAsync(Path.Combine(output, "feature-results.json"), JsonSerializer.Serialize(result, JsonOptions));
        return failures == 0 ? 0 : 1;
    }

    internal static async Task WaitForWebSocketQueueToDrainAsync(
        HttpClient client, IReadOnlyList<Uri> nodes, CancellationToken token)
    {
        // Virtual functions have no per-function gauges. This feature workload
        // owns the cluster, so an all-node zero also covers its virtual queues.
        // Require a new diagnostics observation after receipt reconciliation.
        (double Items, double Cpu)[] boundary = await ReadAsync();
        while (true)
        {
            (double Items, double Cpu)[] observed = await ReadAsync();
            if (observed.Zip(boundary).All(pair => pair.First.Items == 0 && pair.First.Cpu > pair.Second.Cpu))
                return;
            await Task.Delay(250, token);
        }

        Task<(double Items, double Cpu)[]> ReadAsync() => Task.WhenAll(nodes.Select(async node =>
        {
            string metrics = await client.GetStringAsync(new Uri(node, "metrics"), token);
            return (ClusterResourceMonitor.ReadRequiredMetric(metrics, "slimdata_state_queue_items"),
                ClusterResourceMonitor.ReadRequiredMetric(metrics, "slimdata_process_cpu_seconds_total"));
        }));
    }

    internal static JsonContent CreateJobContent(string id) => JsonContent.Create(
        new { Args = new[] { id }, TtlSecondsAfterFinished = 3600 }, options: JsonSerializerOptions.Default);

    internal static FeatureReceipts ReadReceipts(string directory, IReadOnlyList<FeatureSample> samples, string scenario,
        IReadOnlyList<string> functions, int bytes, string checksum)
    {
        HashSet<string> expected = samples.Select(sample => sample.Id).ToHashSet(StringComparer.Ordinal);
        var rows = new List<FeatureReceipt>();
        var corrupt = new List<string>();
        foreach (string path in Directory.GetFiles(directory, "*.jsonl"))
        {
            string contents = File.ReadAllText(path);
            foreach (string line in contents[..(contents.AsSpan().LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement value = document.RootElement;
                string kind = value.GetProperty("event").GetString()!;
                string? id = scenario == "jobs" && value.TryGetProperty("args", out JsonElement args) ? args.EnumerateArray().FirstOrDefault().GetString()
                    : value.TryGetProperty("id", out JsonElement identifier) ? identifier.GetString() : null;
                if (id is null || !expected.Contains(id)) continue;
                string function = scenario == "jobs" ? "job" : value.TryGetProperty("function", out JsonElement owner) ? owner.GetString() ?? "" : "";
                if (scenario != "jobs" && !functions.Contains(function, StringComparer.Ordinal)) { corrupt.Add(id + ": unexpected receiver " + function); continue; }
                if (scenario != "jobs" && (value.GetProperty("bytes").GetInt32() != bytes || value.GetProperty("sha256").GetString() != checksum))
                    corrupt.Add(id + ": length/hash mismatch");
                if (kind == "completed" && value.TryGetProperty("status", out JsonElement status) && status.GetInt32() is < 200 or >= 300)
                    corrupt.Add(id + ": failed receiver status");
                rows.Add(new FeatureReceipt(id, function, kind, value.GetProperty("utc").GetDouble()));
            }
        }
        FeatureReceipt[] completed = rows.Where(row => row.Event is "completed" or "job").ToArray();
        FeatureReceipt[] delivered = rows.Where(row => row.Event is "started" or "job-started").ToArray();
        IEnumerable<string> receivers = scenario == "jobs" ? ["job"] : scenario is "streams" or "ws-events" ? functions : functions.Take(1);
        string[] missing = expected.SelectMany(id => receivers.Select(function => (id, function)))
            .Where(pair => !completed.Any(row => row.Id == pair.id && row.Function == pair.function) || !delivered.Any(row => row.Id == pair.id && row.Function == pair.function))
            .Select(pair => pair.id + ":" + pair.function).ToArray();
        string[] duplicates = completed.GroupBy(row => (row.Id, row.Function)).Where(group => group.Count() > 1).Select(group => group.Key.ToString()).ToArray();
        return new FeatureReceipts(delivered, completed, missing, duplicates, corrupt);
    }

    private static async Task<bool> IsDrainedAsync(HttpClient client, IReadOnlyList<Uri> nodes, string scenario, string stream,
        IReadOnlyList<string> functions, string job, IReadOnlyList<FeatureSample> samples, CancellationToken token)
    {
        if (scenario == "streams")
        {
            foreach (string function in functions)
            {
                using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync(new Uri(nodes[0], $"data/streams/{stream}/functions/{function}"), token));
                if (status.RootElement.GetProperty("Lag").GetInt64() != 0 || status.RootElement.GetProperty("PendingCount").GetInt64() != 0) return false;
            }
        }
        if (scenario == "jobs")
            foreach (Uri node in nodes)
            {
                using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync(new Uri(node, $"job/{job}"), token));
                foreach (FeatureSample sample in samples)
                    if (!status.RootElement.EnumerateArray().Any(row => row.GetProperty("Id").GetString() == sample.QueueId && row.GetProperty("Status").GetString() == "Succeeded")) return false;
            }
        return true;
    }
}

internal sealed record FeatureSample(string Id, double StartedUtc, double Milliseconds, double? FirstByteMilliseconds, string? QueueId, string? Error);
internal sealed record FeatureReceipt(string Id, string Function, string Event, double Utc);
internal sealed record FeatureReceipts(IReadOnlyList<FeatureReceipt> Delivered, IReadOnlyList<FeatureReceipt> Completed,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Duplicates, IReadOnlyList<string> Corrupt);
