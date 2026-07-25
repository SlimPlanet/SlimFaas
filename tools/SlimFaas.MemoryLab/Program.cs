using System.Diagnostics;
using System.Globalization;
using System.Net;
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
    if (scenario is not ("mixed" or "sync" or "async" or "set" or "files"))
        throw new ArgumentException("scenario must be mixed, sync, async, set, or files.");

    int durationSeconds = arguments.GetInt("duration", 60, 1, 86_400);
    int concurrency = arguments.GetInt("concurrency", 12, 1, 1_024);
    int payloadBytes = arguments.GetInt("payload-bytes", 4096, 0, 1_048_576);
    int fileBytes = arguments.GetInt("file-bytes", 262_144, 1, 64 * 1024 * 1024);
    int firstPort = arguments.GetInt("first-port", 30021, 1, 65535);
    int nodeCount = arguments.GetInt("nodes", 3, 1, 32);

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
            duration.Token))
        .ToArray();

    await Task.WhenAll(workers);
    progress.Dispose();
    await progressTask;

    stopwatch.Stop();
    Console.WriteLine(
        "completed={0} failed={1} elapsed={2:F1}s rate={3:F1}/s sync={4} async={5} set={6} files={7}",
        statistics.Completed,
        statistics.Failed,
        stopwatch.Elapsed.TotalSeconds,
        statistics.Completed / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
        statistics.GetCompleted(Operation.Sync),
        statistics.GetCompleted(Operation.Async),
        statistics.GetCompleted(Operation.Set),
        statistics.GetCompleted(Operation.Files));

    return statistics.Failed == 0 ? 0 : 1;
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
    CancellationToken cancellationToken)
{
    long sequence = 0;
    while (!cancellationToken.IsCancellationRequested)
    {
        var operation = SelectOperation(scenario, worker, sequence);
        int node = (int)((sequence + worker) % nodeCount);
        var baseUri = new Uri($"http://127.0.0.1:{firstPort + node}");
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
                cancellationToken);
            statistics.RecordCompleted(operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            statistics.RecordFailure(operation, exception);
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
          load [--scenario mixed|sync|async|set|files] [--duration 60]
               [--concurrency 12] [--payload-bytes 4096] [--file-bytes 262144]
               [--first-port 30021] [--nodes 3]
          report --csv <memory.csv>
        """);
}

enum Operation
{
    Sync,
    Async,
    Set,
    Files
}

sealed class LoadStatistics
{
    private readonly long[] _completed = new long[Enum.GetValues<Operation>().Length];
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

    public long GetCompleted(Operation operation)
        => Volatile.Read(ref _completed[(int)operation]);

    public void RecordCompleted(Operation operation)
        => Interlocked.Increment(ref _completed[(int)operation]);

    public void RecordFailure(Operation operation, Exception exception)
    {
        Interlocked.Increment(ref _failed);
        if (Interlocked.Increment(ref _reportedFailures) <= 10)
            Console.Error.WriteLine($"{operation} failed: {exception.Message}");
    }
}

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
