using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace SlimFaasBenchmark;

internal static class BenchmarkTarget
{
    internal const string RunIdHeader = "X-SlimFaas-Benchmark-Run-Id";
    internal const string SentTicksHeader = "X-SlimFaas-Benchmark-Sent-Utc-Ticks";
    internal const string RequestIdHeader = "X-SlimFaas-Benchmark-Request-Id";

    public static async Task<int> RunAsync(
        CommandArguments arguments,
        CancellationToken cancellationToken = default)
    {
        int port = ResolvePort(arguments);
        var observations = new ConcurrentDictionary<string, ObservationAccumulator>(StringComparer.Ordinal);
        long requestCount = 0;
        long lastScaleWorkTicks = 0;
        long externalScalePressure = 0;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http1));

        WebApplication app = builder.Build();
        app.MapGet("/health", () => Results.Text("OK"));
        app.MapGet("/metrics", () =>
        {
            long lastWork = Volatile.Read(ref lastScaleWorkTicks);
            int scalePressure = lastWork > 0 && DateTime.UtcNow.Ticks - lastWork <= TimeSpan.FromSeconds(30).Ticks
                ? 4
                : 0;
            return Results.Text(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"# TYPE slimfaas_benchmark_target_requests_total counter\n" +
                    $"slimfaas_benchmark_target_requests_total {Volatile.Read(ref requestCount)}\n" +
                    $"# TYPE slimfaas_benchmark_scale_pressure gauge\n" +
                    $"slimfaas_benchmark_scale_pressure {scalePressure}\n" +
                    $"# TYPE slimfaas_benchmark_external_pressure gauge\n" +
                    $"slimfaas_benchmark_external_pressure {Volatile.Read(ref externalScalePressure)}\n"),
                "text/plain; version=0.0.4");
        });
        app.MapPut("/benchmark/external-pressure/{value:long}", (long value) =>
        {
            if (value < 0)
                return Results.BadRequest("pressure must be greater than or equal to zero");
            Interlocked.Exchange(ref externalScalePressure, value);
            return Results.NoContent();
        });
        app.MapGet("/benchmark/observations/{runId}", (string runId) =>
        {
            TargetObservationSummary summary = observations.TryGetValue(runId, out ObservationAccumulator? value)
                ? value.Snapshot(runId)
                : TargetObservationSummary.Empty(runId);
            return Results.Json(summary);
        });
        app.MapDelete("/benchmark/observations/{runId}", (string runId) =>
        {
            observations.TryRemove(runId, out _);
            return Results.NoContent();
        });
        app.MapMethods(
            "/{**path}",
            ["GET", "POST", "PUT", "PATCH", "DELETE"],
            async context =>
            {
                long receivedTicks = DateTime.UtcNow.Ticks;
                Interlocked.Increment(ref requestCount);
                if (context.Request.Path.StartsWithSegments("/work"))
                    Volatile.Write(ref lastScaleWorkTicks, receivedTicks);
                await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);

                int delayMilliseconds = 0;
                if (context.Request.Query.TryGetValue("delayMs", out var rawDelay) &&
                    int.TryParse(rawDelay, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDelay))
                {
                    delayMilliseconds = Math.Clamp(parsedDelay, 0, 60_000);
                }
                if (delayMilliseconds > 0)
                    await Task.Delay(delayMilliseconds, context.RequestAborted);

                long completedTicks = DateTime.UtcNow.Ticks;
                if (context.Request.Headers.TryGetValue(RunIdHeader, out var rawRunId) &&
                    context.Request.Headers.TryGetValue(SentTicksHeader, out var rawSentTicks) &&
                    !string.IsNullOrWhiteSpace(rawRunId) &&
                    long.TryParse(rawSentTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sentTicks))
                {
                    string requestId = context.Request.Headers.TryGetValue(RequestIdHeader, out var rawRequestId) &&
                                       !string.IsNullOrWhiteSpace(rawRequestId)
                        ? rawRequestId.ToString()
                        : Guid.NewGuid().ToString("N");
                    observations.GetOrAdd(rawRunId.ToString(), static _ => new ObservationAccumulator())
                        .Record(
                            requestId,
                            TimeSpan.FromTicks(Math.Max(0, receivedTicks - sentTicks)).TotalMilliseconds,
                            TimeSpan.FromTicks(Math.Max(0, completedTicks - sentTicks)).TotalMilliseconds);
                }

                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });

        Console.WriteLine($"SlimFaas benchmark target listening on http://127.0.0.1:{port}");
        await app.RunAsync(cancellationToken);
        return 0;
    }

    private static int ResolvePort(CommandArguments arguments)
    {
        string? configured = arguments.GetOptional("port") ??
                             Environment.GetEnvironmentVariable("PORT") ??
                             Environment.GetEnvironmentVariable("SLIMFAAS_PORT");
        if (configured is null ||
            !int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
            port is < 1 or > 65535)
        {
            throw new ArgumentException("target requires --port or a valid PORT/SLIMFAAS_PORT environment value.");
        }
        return port;
    }
}

internal sealed class ObservationAccumulator
{
    private readonly object _gate = new();
    private readonly HashSet<string> _requestIds = new(StringComparer.Ordinal);
    private readonly List<double> _arrivalMilliseconds = [];
    private readonly List<double> _completionMilliseconds = [];
    private int _count;
    private int _duplicates;

    public void Record(string requestId, double arrivalMilliseconds, double completionMilliseconds)
    {
        lock (_gate)
        {
            _count++;
            if (!_requestIds.Add(requestId))
            {
                _duplicates++;
                return;
            }
            _arrivalMilliseconds.Add(arrivalMilliseconds);
            _completionMilliseconds.Add(completionMilliseconds);
        }
    }

    public TargetObservationSummary Snapshot(string runId)
    {
        lock (_gate)
        {
            double[] arrivals = _arrivalMilliseconds.Order().ToArray();
            double[] completions = _completionMilliseconds.Order().ToArray();
            return new TargetObservationSummary(
                runId,
                _count,
                Statistics.Percentile(arrivals, 0.50),
                Statistics.Percentile(arrivals, 0.95),
                Statistics.Percentile(arrivals, 0.99),
                Statistics.Percentile(completions, 0.50),
                Statistics.Percentile(completions, 0.95),
                Statistics.Percentile(completions, 0.99),
                _requestIds.Count,
                _duplicates,
                arrivals.Length == 0 ? 0 : arrivals.Average(),
                completions.Length == 0 ? 0 : completions.Average());
        }
    }
}

internal sealed record TargetObservationSummary(
    string RunId,
    int Count,
    double ArrivalP50Milliseconds,
    double ArrivalP95Milliseconds,
    double ArrivalP99Milliseconds,
    double CompletionP50Milliseconds,
    double CompletionP95Milliseconds,
    double CompletionP99Milliseconds,
    int UniqueCount = 0,
    int DuplicateCount = 0,
    double ArrivalMeanMilliseconds = 0,
    double CompletionMeanMilliseconds = 0)
{
    public static TargetObservationSummary Empty(string runId) => new(runId, 0, 0, 0, 0, 0, 0, 0);
}
