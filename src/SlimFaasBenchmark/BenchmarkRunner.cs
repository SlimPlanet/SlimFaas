using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SlimFaasBenchmark;

internal static class BenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(CommandArguments arguments)
    {
        RunnerOptions options = RunnerOptions.Parse(arguments);
        Directory.CreateDirectory(options.OutputDirectory);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = Math.Max(128, options.Concurrency.Max() * 4),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseProxy = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        await EnsureReadyAsync(client, options);

        var latencyRuns = new List<LatencyRunResult>();
        string samplesPath = Path.Combine(options.OutputDirectory, "latency-samples.csv");
        await using var samplesStream = new FileStream(
            samplesPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        await using var samplesWriter = new StreamWriter(samplesStream, new UTF8Encoding(false));
        await samplesWriter.WriteLineAsync(
            "mode,payload_bytes,concurrency,repetition,success,latency_ms,error");
        int caseIndex = 0;
        foreach (int payloadBytes in options.PayloadBytes)
        {
            byte[] payload = CreatePayload(payloadBytes);
            foreach (int concurrency in options.Concurrency)
            {
                for (var repetition = 1; repetition <= options.Repetitions; repetition++)
                {
                    Console.WriteLine(
                        $"Latency payload={payloadBytes} bytes concurrency={concurrency} repetition={repetition}/{options.Repetitions}");

                    // Alternate the pair order so CPU frequency and temperature drift do not always favor one route.
                    string[] pairedModes = caseIndex++ % 2 == 0
                        ? ["direct", "sync"]
                        : ["sync", "direct"];
                    foreach (string mode in pairedModes.Append("async"))
                    {
                        LatencyCaseData caseData = await RunLatencyCaseAsync(
                            client,
                            options,
                            mode,
                            payload,
                            concurrency,
                            repetition);
                        LatencyRunResult result = caseData.Result;
                        latencyRuns.Add(result);
                        await AppendSamplesCsvAsync(samplesWriter, caseData.Samples);
                        Console.WriteLine(FormattableString.Invariant(
                            $"  {mode,-6} rate={result.RatePerSecond,9:F1}/s p50={result.P50Milliseconds,8:F3}ms p95={result.P95Milliseconds,8:F3}ms p99={result.P99Milliseconds,8:F3}ms failed={result.Failed}"));
                    }
                }
            }
        }

        await samplesWriter.FlushAsync();
        IReadOnlyList<LatencyAggregateResult> latency = AggregateLatency(latencyRuns);
        IReadOnlyList<SyncOverheadResult> overhead = ComputeSyncOverhead(latency);

        Console.WriteLine("Running scale-to-zero and PromQL scale-out burst...");
        var scaleTimeline = new List<ScaleTimelineSample>();
        ScaleResult scaling = await RunScaleBenchmarkAsync(client, options, scaleTimeline);

        var settings = new BenchmarkSettings(
            options.SlimFaasUrl.ToString().TrimEnd('/'),
            options.DirectUrl.ToString().TrimEnd('/'),
            options.NodeUrls.Select(uri => uri.ToString().TrimEnd('/')).ToArray(),
            options.Function,
            options.ScaleFunction,
            options.DurationSeconds,
            options.WarmupSeconds,
            options.Repetitions,
            options.PayloadBytes,
            options.Concurrency,
            options.ScaleMessages,
            options.ScaleConcurrency,
            options.ScaleTargetReplicas);
        var report = new BenchmarkReport(
            startedAt,
            DateTimeOffset.UtcNow,
            Environment.OSVersion.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            settings,
            latencyRuns,
            latency,
            overhead,
            scaling);

        await WriteArtifactsAsync(options.OutputDirectory, report, scaleTimeline);
        PrintSummary(overhead, scaling, options.OutputDirectory);
        return latencyRuns.Any(run => run.Failed > 0) || scaling.Failed > 0 || scaling.TimedOut ? 1 : 0;
    }

    private static async Task EnsureReadyAsync(HttpClient client, RunnerOptions options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await WaitUntilAsync(
            async () => await IsSuccessAsync(client, new Uri(options.SlimFaasUrl, "ready"), timeout.Token),
            TimeSpan.FromMilliseconds(500),
            timeout.Token,
            "SlimFaas entrypoint did not become ready within 180 seconds.");
        await WaitUntilAsync(
            async () => await IsSuccessAsync(client, new Uri(options.DirectUrl, "health"), timeout.Token),
            TimeSpan.FromMilliseconds(250),
            timeout.Token,
            "The direct benchmark target did not become ready within 180 seconds.");
        await WaitUntilAsync(
            async () =>
            {
                FunctionStatusDto? status = await GetFunctionStatusAsync(
                    client,
                    options.SlimFaasUrl,
                    options.Function,
                    timeout.Token);
                return status is { NumberReady: > 0 };
            },
            TimeSpan.FromMilliseconds(250),
            timeout.Token,
            $"Function '{options.Function}' did not become ready within 180 seconds.");

        // Let topology and Raft synchronization settle before collecting latency samples.
        await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
    }

    private static async Task<LatencyCaseData> RunLatencyCaseAsync(
        HttpClient client,
        RunnerOptions options,
        string mode,
        byte[] payload,
        int concurrency,
        int repetition)
    {
        Uri uri = mode switch
        {
            "direct" => new Uri(options.DirectUrl, "echo"),
            "sync" => new Uri(options.SlimFaasUrl, $"function/{options.Function}/echo"),
            "async" => new Uri(options.SlimFaasUrl, $"async-function/{options.Function}/echo"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        if (options.WarmupSeconds > 0)
        {
            await ExecuteTimedLoadAsync(
                client,
                uri,
                mode,
                payload,
                concurrency,
                TimeSpan.FromSeconds(options.WarmupSeconds),
                repetition,
                runId: null,
                samples: null);
            if (mode == "async")
                await WaitForQueueToDrainAsync(client, options.NodeUrls, options.Function, options.AsyncDrainTimeout);
        }

        string? runId = mode == "async"
            ? $"async-{payload.Length}-{concurrency}-{repetition}-{Guid.NewGuid():N}"
            : null;
        var runSamples = new ConcurrentQueue<RequestSample>();
        TimeSpan elapsed = await ExecuteTimedLoadAsync(
            client,
            uri,
            mode,
            payload,
            concurrency,
            TimeSpan.FromSeconds(options.DurationSeconds),
            repetition,
            runId,
            runSamples);
        RequestSample[] snapshot = runSamples.ToArray();
        double[] successful = snapshot
            .Where(sample => sample.Success)
            .Select(sample => sample.LatencyMilliseconds)
            .Order()
            .ToArray();
        long failed = snapshot.LongLength - successful.LongLength;

        TargetObservationSummary? asyncDelivery = null;
        if (mode == "async" && runId is not null)
        {
            asyncDelivery = await WaitForAsyncDeliveryAsync(
                client,
                options.DirectUrl,
                runId,
                checked((int)successful.LongLength),
                options.AsyncDrainTimeout);
            await WaitForQueueToDrainAsync(client, options.NodeUrls, options.Function, options.AsyncDrainTimeout);
            using HttpResponseMessage _ = await client.DeleteAsync(
                new Uri(options.DirectUrl, $"benchmark/observations/{runId}"));
        }

        var result = new LatencyRunResult(
            mode,
            payload.Length,
            concurrency,
            repetition,
            successful.LongLength,
            failed,
            elapsed.TotalSeconds,
            successful.LongLength / Math.Max(0.001, elapsed.TotalSeconds),
            Statistics.Percentile(successful, 0.50),
            Statistics.Percentile(successful, 0.95),
            Statistics.Percentile(successful, 0.99),
            successful.Length == 0 ? 0 : successful[^1],
            asyncDelivery);
        return new LatencyCaseData(result, snapshot);
    }

    private static async Task<TimeSpan> ExecuteTimedLoadAsync(
        HttpClient client,
        Uri uri,
        string mode,
        byte[] payload,
        int concurrency,
        TimeSpan duration,
        int repetition,
        string? runId,
        ConcurrentQueue<RequestSample>? samples)
    {
        long started = Stopwatch.GetTimestamp();
        long deadline = started + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        Task[] workers = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    long requestStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                        {
                            Content = new ByteArrayContent(payload)
                        };
                        request.Content.Headers.ContentType =
                            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                        if (runId is not null)
                        {
                            request.Headers.TryAddWithoutValidation(BenchmarkTarget.RunIdHeader, runId);
                            request.Headers.TryAddWithoutValidation(
                                BenchmarkTarget.SentTicksHeader,
                                DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                        }

                        using HttpResponseMessage response = await client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead);
                        await response.Content.CopyToAsync(Stream.Null);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException(
                                $"HTTP {(int)response.StatusCode} ({response.StatusCode}) from {uri}");
                        }

                        samples?.Enqueue(new RequestSample(
                            mode,
                            payload.Length,
                            concurrency,
                            repetition,
                            true,
                            Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds,
                            null));
                    }
                    catch (Exception exception)
                    {
                        samples?.Enqueue(new RequestSample(
                            mode,
                            payload.Length,
                            concurrency,
                            repetition,
                            false,
                            Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds,
                            exception.Message));
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(workers);
        return Stopwatch.GetElapsedTime(started);
    }

    private static async Task<TargetObservationSummary> WaitForAsyncDeliveryAsync(
        HttpClient client,
        Uri directUrl,
        string runId,
        int expected,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        TargetObservationSummary latest = TargetObservationSummary.Empty(runId);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                latest = await client.GetFromJsonAsync<TargetObservationSummary>(
                             new Uri(directUrl, $"benchmark/observations/{runId}"),
                             JsonOptions,
                             cancellation.Token) ?? TargetObservationSummary.Empty(runId);
                if (latest.Count >= expected)
                    return latest;
                await Task.Delay(100, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        throw new TimeoutException(
            $"Async run '{runId}' delivered {latest.Count}/{expected} messages after {timeout.TotalSeconds:F0}s.");
    }

    private static async Task WaitForQueueToDrainAsync(
        HttpClient client,
        IReadOnlyList<Uri> nodeUrls,
        string function,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var stableZeros = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                QueueDepth depth = await GetQueueDepthAsync(client, nodeUrls, function, cancellation.Token);
                stableZeros = depth.HasValue && depth.Total <= 0 ? stableZeros + 1 : 0;
                if (stableZeros >= 2)
                    return;
                await Task.Delay(250, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        throw new TimeoutException($"Queue for '{function}' did not drain after {timeout.TotalSeconds:F0}s.");
    }

    private static IReadOnlyList<LatencyAggregateResult> AggregateLatency(
        IReadOnlyList<LatencyRunResult> runs)
    {
        return runs.GroupBy(run => new { run.Mode, run.PayloadBytes, run.Concurrency })
            .OrderBy(group => group.Key.PayloadBytes)
            .ThenBy(group => group.Key.Concurrency)
            .ThenBy(group => group.Key.Mode, StringComparer.Ordinal)
            .Select(group =>
            {
                LatencyRunResult[] groupedRuns = group.ToArray();
                TargetObservationSummary[] asyncDeliveries = group
                    .Select(run => run.AsyncDelivery)
                    .Where(summary => summary is not null)
                    .Cast<TargetObservationSummary>()
                    .ToArray();
                return new LatencyAggregateResult(
                    group.Key.Mode,
                    group.Key.PayloadBytes,
                    group.Key.Concurrency,
                    groupedRuns.Length,
                    group.Sum(run => run.Completed),
                    group.Sum(run => run.Failed),
                    group.Average(run => run.RatePerSecond),
                    Statistics.Median(groupedRuns.Select(run => run.P50Milliseconds)),
                    Statistics.Median(groupedRuns.Select(run => run.P95Milliseconds)),
                    Statistics.Median(groupedRuns.Select(run => run.P99Milliseconds)),
                    groupedRuns.Max(run => run.MaxMilliseconds),
                    AsyncMedian(asyncDeliveries, summary => summary.ArrivalP50Milliseconds),
                    AsyncMedian(asyncDeliveries, summary => summary.ArrivalP95Milliseconds),
                    AsyncMedian(asyncDeliveries, summary => summary.ArrivalP99Milliseconds),
                    AsyncMedian(asyncDeliveries, summary => summary.CompletionP50Milliseconds),
                    AsyncMedian(asyncDeliveries, summary => summary.CompletionP95Milliseconds),
                    AsyncMedian(asyncDeliveries, summary => summary.CompletionP99Milliseconds),
                    asyncDeliveries.Length == 0 ? null : asyncDeliveries.Sum(summary => summary.Count));
            })
            .ToArray();
    }

    private static double? AsyncMedian(
        IReadOnlyList<TargetObservationSummary> values,
        Func<TargetObservationSummary, double> selector) =>
        values.Count == 0 ? null : Statistics.Median(values.Select(selector));

    private static IReadOnlyList<SyncOverheadResult> ComputeSyncOverhead(
        IReadOnlyList<LatencyAggregateResult> latency)
    {
        return latency.Where(result => result.Mode == "direct")
            .Select(direct =>
            {
                LatencyAggregateResult sync = latency.Single(result =>
                    result.Mode == "sync" &&
                    result.PayloadBytes == direct.PayloadBytes &&
                    result.Concurrency == direct.Concurrency);
                return new SyncOverheadResult(
                    direct.PayloadBytes,
                    direct.Concurrency,
                    direct.P50Milliseconds,
                    sync.P50Milliseconds,
                    sync.P50Milliseconds - direct.P50Milliseconds,
                    Statistics.PercentDelta(direct.P50Milliseconds, sync.P50Milliseconds),
                    direct.P95Milliseconds,
                    sync.P95Milliseconds,
                    sync.P95Milliseconds - direct.P95Milliseconds,
                    Statistics.PercentDelta(direct.P95Milliseconds, sync.P95Milliseconds),
                    direct.P99Milliseconds,
                    sync.P99Milliseconds,
                    sync.P99Milliseconds - direct.P99Milliseconds,
                    Statistics.PercentDelta(direct.P99Milliseconds, sync.P99Milliseconds),
                    direct.MeanRatePerSecond,
                    sync.MeanRatePerSecond,
                    direct.MeanRatePerSecond <= 0 ? 0 : sync.MeanRatePerSecond / direct.MeanRatePerSecond);
            })
            .OrderBy(result => result.PayloadBytes)
            .ThenBy(result => result.Concurrency)
            .ToArray();
    }

    private static async Task<ScaleResult> RunScaleBenchmarkAsync(
        HttpClient client,
        RunnerOptions options,
        List<ScaleTimelineSample> timeline)
    {
        using var waitForZero = new CancellationTokenSource(options.ScaleTimeout);
        await WaitUntilAsync(
            async () =>
            {
                (int requested, int ready) = await GetMaximumFunctionStatusAsync(
                    client,
                    options.NodeUrls,
                    options.ScaleFunction,
                    waitForZero.Token);
                return requested == 0 && ready == 0;
            },
            TimeSpan.FromMilliseconds(250),
            waitForZero.Token,
            $"Scale function '{options.ScaleFunction}' did not reach zero before the burst.");

        byte[] payload = CreatePayload(256);
        Uri scaleUri = new(options.SlimFaasUrl,
            $"async-function/{options.ScaleFunction}/work?delayMs=250");
        long started = Stopwatch.GetTimestamp();
        Task<BurstResult> burstTask = RunBurstAsync(
            client,
            scaleUri,
            payload,
            options.ScaleMessages,
            options.ScaleConcurrency,
            started);

        double? requestedOne = null;
        double? readyOne = null;
        double? requestedTarget = null;
        double? readyTarget = null;
        double? drained = null;
        double peakQueue = 0;
        bool sawPositiveQueue = false;
        bool timedOut = false;
        using var timeout = new CancellationTokenSource(options.ScaleTimeout);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Task<(int Requested, int Ready)> statusTask = GetMaximumFunctionStatusAsync(
                    client,
                    options.NodeUrls,
                    options.ScaleFunction,
                    timeout.Token);
                Task<QueueDepth> queueTask = GetQueueDepthAsync(
                    client,
                    options.NodeUrls,
                    options.ScaleFunction,
                    timeout.Token);
                await Task.WhenAll(statusTask, queueTask);
                (int requested, int ready) = statusTask.Result;
                QueueDepth queue = queueTask.Result;
                timeline.Add(new ScaleTimelineSample(
                    elapsed,
                    requested,
                    ready,
                    queue.Ready,
                    queue.InFlight,
                    queue.Retry));

                if (requested >= 1 && requestedOne is null)
                    requestedOne = elapsed;
                if (ready >= 1 && readyOne is null)
                    readyOne = elapsed;
                if (requested >= options.ScaleTargetReplicas && requestedTarget is null)
                    requestedTarget = elapsed;
                if (ready >= options.ScaleTargetReplicas && readyTarget is null)
                    readyTarget = elapsed;
                if (queue.Ready is { } readyQueue)
                    peakQueue = Math.Max(peakQueue, readyQueue);
                if (queue.HasValue && queue.Total > 0)
                    sawPositiveQueue = true;
                if (burstTask.IsCompleted && sawPositiveQueue && queue.HasValue && queue.Total <= 0)
                    drained ??= elapsed;

                if (burstTask.IsCompleted && readyTarget.HasValue && drained.HasValue)
                    break;
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = true;
        }

        if (!burstTask.IsCompleted)
        {
            try
            {
                await burstTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }
        }
        BurstResult burst = burstTask.IsCompletedSuccessfully
            ? burstTask.Result
            : new BurstResult(0, options.ScaleMessages, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new ScaleResult(
            options.ScaleMessages,
            options.ScaleConcurrency,
            options.ScaleTargetReplicas,
            burst.Accepted,
            burst.Failed,
            burst.FirstAcceptedMilliseconds,
            burst.AllAcceptedMilliseconds,
            requestedOne,
            readyOne,
            requestedTarget,
            readyTarget,
            drained,
            peakQueue,
            timedOut || !readyTarget.HasValue || !drained.HasValue);
    }

    private static async Task<BurstResult> RunBurstAsync(
        HttpClient client,
        Uri uri,
        byte[] payload,
        int messages,
        int concurrency,
        long benchmarkStarted)
    {
        long accepted = 0;
        long failed = 0;
        long firstAcceptedTimestamp = long.MaxValue;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, messages),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (_, cancellationToken) =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = new ByteArrayContent(payload)
                    };
                    request.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    using HttpResponseMessage response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    await response.Content.CopyToAsync(Stream.Null, cancellationToken);
                    if (response.StatusCode != HttpStatusCode.Accepted)
                        throw new HttpRequestException($"Expected HTTP 202, received {(int)response.StatusCode}.");
                    Interlocked.Increment(ref accepted);
                    UpdateMinimum(ref firstAcceptedTimestamp, Stopwatch.GetTimestamp());
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                }
            });

        double? firstAccepted = firstAcceptedTimestamp == long.MaxValue
            ? null
            : Stopwatch.GetElapsedTime(benchmarkStarted, firstAcceptedTimestamp).TotalMilliseconds;
        return new BurstResult(
            checked((int)accepted),
            checked((int)failed),
            firstAccepted,
            Stopwatch.GetElapsedTime(benchmarkStarted).TotalMilliseconds);
    }

    private static void UpdateMinimum(ref long target, long value)
    {
        long current = Volatile.Read(ref target);
        while (value < current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static async Task<(int Requested, int Ready)> GetMaximumFunctionStatusAsync(
        HttpClient client,
        IReadOnlyList<Uri> nodeUrls,
        string function,
        CancellationToken cancellationToken)
    {
        FunctionStatusDto?[] statuses = await Task.WhenAll(nodeUrls.Select(node =>
            GetFunctionStatusAsync(client, node, function, cancellationToken)));
        FunctionStatusDto[] available = statuses.Where(status => status is not null)
            .Cast<FunctionStatusDto>()
            .ToArray();
        return available.Length == 0
            ? (0, 0)
            : (available.Max(status => status.NumberRequested), available.Max(status => status.NumberReady));
    }

    private static async Task<FunctionStatusDto?> GetFunctionStatusAsync(
        HttpClient client,
        Uri baseUri,
        string function,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync(
                new Uri(baseUri, $"status-function/{function}"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<FunctionStatusDto>(JsonOptions, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<QueueDepth> GetQueueDepthAsync(
        HttpClient client,
        IReadOnlyList<Uri> nodeUrls,
        string function,
        CancellationToken cancellationToken)
    {
        QueueDepth[] depths = await Task.WhenAll(nodeUrls.Select(node =>
            GetNodeQueueDepthAsync(client, node, function, cancellationToken)));
        return new QueueDepth(
            MaxNullable(depths.Select(depth => depth.Ready)),
            MaxNullable(depths.Select(depth => depth.InFlight)),
            MaxNullable(depths.Select(depth => depth.Retry)));
    }

    private static async Task<QueueDepth> GetNodeQueueDepthAsync(
        HttpClient client,
        Uri nodeUrl,
        string function,
        CancellationToken cancellationToken)
    {
        try
        {
            string metrics = await client.GetStringAsync(new Uri(nodeUrl, "metrics"), cancellationToken);
            return new QueueDepth(
                ParseMetric(metrics, "slimfaas_function_queue_ready_items", function),
                ParseMetric(metrics, "slimfaas_function_queue_in_flight_items", function),
                ParseMetric(metrics, "slimfaas_function_queue_retry_pending_items", function));
        }
        catch (HttpRequestException)
        {
            return new QueueDepth(null, null, null);
        }
    }

    private static double? ParseMetric(string metrics, string metricName, string function)
    {
        string functionLabel = $"function=\"{function}\"";
        foreach (ReadOnlySpan<char> line in metrics.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith(metricName, StringComparison.Ordinal) ||
                !line.Contains(functionLabel, StringComparison.Ordinal))
            {
                continue;
            }

            int separator = line.LastIndexOf(' ');
            if (separator >= 0 &&
                double.TryParse(line[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }
        }
        return null;
    }

    private static double? MaxNullable(IEnumerable<double?> values)
    {
        double[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static async Task<bool> IsSuccessAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan delay,
        CancellationToken cancellationToken,
        string timeoutMessage)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (await predicate())
                    return;
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        throw new TimeoutException(timeoutMessage);
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)('a' + (index % 26));
        return payload;
    }

    private static async Task WriteArtifactsAsync(
        string outputDirectory,
        BenchmarkReport report,
        IReadOnlyList<ScaleTimelineSample> timeline)
    {
        string jsonPath = Path.Combine(outputDirectory, "results.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        await WriteScaleTimelineCsvAsync(Path.Combine(outputDirectory, "scaling-timeline.csv"), timeline);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "summary.md"), BuildMarkdown(report));
    }

    private static async Task AppendSamplesCsvAsync(
        StreamWriter writer,
        IReadOnlyList<RequestSample> samples)
    {
        foreach (RequestSample sample in samples)
        {
            await writer.WriteLineAsync(string.Join(',',
                sample.Mode,
                sample.PayloadBytes.ToString(CultureInfo.InvariantCulture),
                sample.Concurrency.ToString(CultureInfo.InvariantCulture),
                sample.Repetition.ToString(CultureInfo.InvariantCulture),
                sample.Success ? "true" : "false",
                sample.LatencyMilliseconds.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(sample.Error)));
        }
        await writer.FlushAsync();
    }

    private static async Task WriteScaleTimelineCsvAsync(
        string path,
        IReadOnlyList<ScaleTimelineSample> timeline)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync(
            "elapsed_ms,requested_replicas,ready_replicas,queue_ready,queue_in_flight,queue_retry");
        foreach (ScaleTimelineSample sample in timeline)
        {
            await writer.WriteLineAsync(string.Join(',',
                sample.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                sample.RequestedReplicas.ToString(CultureInfo.InvariantCulture),
                sample.ReadyReplicas.ToString(CultureInfo.InvariantCulture),
                NullableCsv(sample.ReadyQueue),
                NullableCsv(sample.InFlightQueue),
                NullableCsv(sample.RetryQueue)));
        }
    }

    private static string BuildMarkdown(BenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SlimFaas local latency and scaling benchmark");
        builder.AppendLine();
        builder.AppendLine($"Generated at `{report.FinishedAtUtc:O}` on `{report.OperatingSystem}` with `{report.Framework}`.");
        builder.AppendLine();
        builder.AppendLine("## Synchronous overhead");
        builder.AppendLine();
        builder.AppendLine("| payload | concurrency | direct p50 | SlimFaas p50 | added p50 | direct p95 | SlimFaas p95 | added p95 | direct p99 | SlimFaas p99 | added p99 | rate ratio |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (SyncOverheadResult row in report.SyncOverhead)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| {FormatBytes(row.PayloadBytes)} | {row.Concurrency} | {row.DirectP50Milliseconds:F3} ms | {row.SlimFaasP50Milliseconds:F3} ms | {row.AddedP50Milliseconds:+0.000;-0.000;0.000} ms ({row.AddedP50Percent:+0.0;-0.0;0.0}%) | {row.DirectP95Milliseconds:F3} ms | {row.SlimFaasP95Milliseconds:F3} ms | {row.AddedP95Milliseconds:+0.000;-0.000;0.000} ms ({row.AddedP95Percent:+0.0;-0.0;0.0}%) | {row.DirectP99Milliseconds:F3} ms | {row.SlimFaasP99Milliseconds:F3} ms | {row.AddedP99Milliseconds:+0.000;-0.000;0.000} ms ({row.AddedP99Percent:+0.0;-0.0;0.0}%) | {row.RateRatio:P1} |"));
        }

        builder.AppendLine();
        builder.AppendLine("## Asynchronous ingress and delivery");
        builder.AppendLine();
        builder.AppendLine("The HTTP columns measure client-to-202 enqueue latency. Target columns measure client send to handler arrival and completion.");
        builder.AppendLine();
        builder.AppendLine("| payload | concurrency | accepted | failed | rate | HTTP p50 | HTTP p95 | HTTP p99 | arrival p50 | arrival p95 | arrival p99 | completion p50 | completion p95 | completion p99 |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (LatencyAggregateResult row in report.Latency.Where(result => result.Mode == "async"))
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| {FormatBytes(row.PayloadBytes)} | {row.Concurrency} | {row.Completed} | {row.Failed} | {row.MeanRatePerSecond:F1}/s | {row.P50Milliseconds:F3} ms | {row.P95Milliseconds:F3} ms | {row.P99Milliseconds:F3} ms | {FormatNullable(row.AsyncArrivalP50Milliseconds)} | {FormatNullable(row.AsyncArrivalP95Milliseconds)} | {FormatNullable(row.AsyncArrivalP99Milliseconds)} | {FormatNullable(row.AsyncCompletionP50Milliseconds)} | {FormatNullable(row.AsyncCompletionP95Milliseconds)} | {FormatNullable(row.AsyncCompletionP99Milliseconds)} |"));
        }

        ScaleResult scale = report.Scaling;
        builder.AppendLine();
        builder.AppendLine("## Scaling burst");
        builder.AppendLine();
        builder.AppendLine($"Burst: `{scale.Messages}` messages, concurrency `{scale.Concurrency}`, target `{scale.TargetReplicas}` replicas.");
        builder.AppendLine();
        builder.AppendLine("| milestone from first send | observed time |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| first HTTP 202 | {FormatNullable(scale.FirstAcceptedMilliseconds)} |");
        builder.AppendLine(FormattableString.Invariant(
            $"| all HTTP 202 responses | {scale.AllAcceptedMilliseconds:F3} ms |"));
        builder.AppendLine($"| desired replicas >= 1 | {FormatNullable(scale.RequestedOneMilliseconds)} |");
        builder.AppendLine($"| ready replicas >= 1 | {FormatNullable(scale.ReadyOneMilliseconds)} |");
        builder.AppendLine($"| desired replicas >= {scale.TargetReplicas} | {FormatNullable(scale.RequestedTargetMilliseconds)} |");
        builder.AppendLine($"| ready replicas >= {scale.TargetReplicas} | {FormatNullable(scale.ReadyTargetMilliseconds)} |");
        builder.AppendLine($"| queue drained | {FormatNullable(scale.QueueDrainedMilliseconds)} |");
        builder.AppendLine();
        builder.AppendLine(FormattableString.Invariant(
            $"Peak observed ready queue: `{scale.PeakReadyQueue:F0}`. Timed out: `{scale.TimedOut}`."));
        builder.AppendLine();
        builder.AppendLine("## Interpretation boundaries");
        builder.AppendLine();
        builder.AppendLine("- Direct and sync calls hit the exact same target process; their delta isolates the warm proxy path on this host.");
        builder.AppendLine("- Async delivery uses UTC timestamps across local processes and includes enqueue, queue wait, dispatch, and request-body transfer.");
        builder.AppendLine("- The async payload matrix includes 2 MiB, above SlimFaas' 1 MiB body-offload threshold.");
        builder.AppendLine("- Scaling uses native local processes. It measures SlimFaas decisions and process readiness, not Kubernetes scheduling or image pulls.");
        builder.AppendLine("- Function status endpoints have short caches; scaling milestones are observed values rather than internal nanosecond timestamps.");
        return builder.ToString();
    }

    private static void PrintSummary(
        IReadOnlyList<SyncOverheadResult> overhead,
        ScaleResult scaling,
        string outputDirectory)
    {
        Console.WriteLine();
        Console.WriteLine("payload,concurrency,added_p50_ms,added_p95_ms,added_p99_ms,rate_ratio");
        foreach (SyncOverheadResult row in overhead)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"{row.PayloadBytes},{row.Concurrency},{row.AddedP50Milliseconds:F3},{row.AddedP95Milliseconds:F3},{row.AddedP99Milliseconds:F3},{row.RateRatio:F3}"));
        }
        Console.WriteLine(FormattableString.Invariant(
            $"Scaling: ready 1={FormatNullable(scaling.ReadyOneMilliseconds)}, ready {scaling.TargetReplicas}={FormatNullable(scaling.ReadyTargetMilliseconds)}, drained={FormatNullable(scaling.QueueDrainedMilliseconds)}"));
        Console.WriteLine($"Benchmark artifacts: {Path.GetFullPath(outputDirectory)}");
    }

    private static string FormatBytes(int bytes) => bytes switch
    {
        >= 1024 * 1024 when bytes % (1024 * 1024) == 0 => $"{bytes / (1024 * 1024)} MiB",
        >= 1024 when bytes % 1024 == 0 => $"{bytes / 1024} KiB",
        _ => $"{bytes} B"
    };

    private static string FormatNullable(double? value) =>
        value.HasValue ? FormattableString.Invariant($"{value.Value:F3} ms") : "not observed";

    private static string NullableCsv(double? value) =>
        value?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private sealed record BurstResult(
        int Accepted,
        int Failed,
        double? FirstAcceptedMilliseconds,
        double AllAcceptedMilliseconds);

    private sealed record RunnerOptions(
        Uri SlimFaasUrl,
        Uri DirectUrl,
        IReadOnlyList<Uri> NodeUrls,
        string Function,
        string ScaleFunction,
        int DurationSeconds,
        int WarmupSeconds,
        int Repetitions,
        IReadOnlyList<int> PayloadBytes,
        IReadOnlyList<int> Concurrency,
        TimeSpan AsyncDrainTimeout,
        int ScaleMessages,
        int ScaleConcurrency,
        int ScaleTargetReplicas,
        TimeSpan ScaleTimeout,
        string OutputDirectory)
    {
        public static RunnerOptions Parse(CommandArguments arguments)
        {
            Uri slimFaasUrl = ParseBaseUri(arguments.Get("slimfaas-url", "http://127.0.0.1:31020"));
            Uri directUrl = ParseBaseUri(arguments.Get("direct-url", "http://127.0.0.1:31080"));
            IReadOnlyList<Uri> nodeUrls = arguments.GetUriList(
                "node-urls",
                "http://127.0.0.1:31021,http://127.0.0.1:31022,http://127.0.0.1:31023");
            int asyncDrainSeconds = arguments.GetInt("async-drain-timeout", 180, 1, 3600);
            int scaleTimeoutSeconds = arguments.GetInt("scale-timeout", 120, 1, 3600);
            return new RunnerOptions(
                slimFaasUrl,
                directUrl,
                nodeUrls,
                arguments.Get("function", "benchmark-latency"),
                arguments.Get("scale-function", "benchmark-scale"),
                arguments.GetInt("duration", 10, 1, 3600),
                arguments.GetInt("warmup", 2, 0, 600),
                arguments.GetInt("repetitions", 3, 1, 100),
                arguments.GetIntList("payload-bytes", "64,4096,262144,2097152", 0, 32 * 1024 * 1024),
                arguments.GetIntList("concurrency", "1,16", 1, 1024),
                TimeSpan.FromSeconds(asyncDrainSeconds),
                arguments.GetInt("scale-messages", 200, 1, 1_000_000),
                arguments.GetInt("scale-concurrency", 32, 1, 1024),
                arguments.GetInt("scale-target-replicas", 4, 2, 1000),
                TimeSpan.FromSeconds(scaleTimeoutSeconds),
                Path.GetFullPath(arguments.Get(
                    "output",
                    Path.Combine("artifacts", "slimfaas-local-benchmark", "manual"))));
        }

        private static Uri ParseBaseUri(string value) =>
            Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri)
                ? uri
                : throw new ArgumentException($"Invalid URI '{value}'.");
    }
}
