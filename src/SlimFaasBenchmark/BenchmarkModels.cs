namespace SlimFaasBenchmark;

internal readonly record struct RequestSample(
    string Mode,
    int PayloadBytes,
    int Concurrency,
    int Repetition,
    bool Success,
    double LatencyMilliseconds,
    string? Error);

internal sealed record LatencyCaseData(
    LatencyRunResult Result,
    IReadOnlyList<RequestSample> Samples);

internal sealed record LatencyRunResult(
    string Mode,
    int PayloadBytes,
    int Concurrency,
    int Repetition,
    long Completed,
    long Failed,
    double ElapsedSeconds,
    double RatePerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds,
    TargetObservationSummary? AsyncDelivery);

internal sealed record LatencyAggregateResult(
    string Mode,
    int PayloadBytes,
    int Concurrency,
    int Repetitions,
    long Completed,
    long Failed,
    double MeanRatePerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds,
    double? AsyncArrivalP50Milliseconds,
    double? AsyncArrivalP95Milliseconds,
    double? AsyncArrivalP99Milliseconds,
    double? AsyncCompletionP50Milliseconds,
    double? AsyncCompletionP95Milliseconds,
    double? AsyncCompletionP99Milliseconds,
    int? AsyncObserved);

internal sealed record SyncOverheadResult(
    int PayloadBytes,
    int Concurrency,
    double DirectP50Milliseconds,
    double SlimFaasP50Milliseconds,
    double AddedP50Milliseconds,
    double AddedP50Percent,
    double DirectP95Milliseconds,
    double SlimFaasP95Milliseconds,
    double AddedP95Milliseconds,
    double AddedP95Percent,
    double DirectP99Milliseconds,
    double SlimFaasP99Milliseconds,
    double AddedP99Milliseconds,
    double AddedP99Percent,
    double DirectRatePerSecond,
    double SlimFaasRatePerSecond,
    double RateRatio);

internal sealed record ScaleTimelineSample(
    double ElapsedMilliseconds,
    int RequestedReplicas,
    int ReadyReplicas,
    double? ReadyQueue,
    double? InFlightQueue,
    double? RetryQueue);

internal sealed record ScaleResult(
    int Messages,
    int Concurrency,
    int TargetReplicas,
    int Accepted,
    int Failed,
    double? FirstAcceptedMilliseconds,
    double AllAcceptedMilliseconds,
    double? RequestedOneMilliseconds,
    double? ReadyOneMilliseconds,
    double? RequestedTargetMilliseconds,
    double? ReadyTargetMilliseconds,
    double? QueueDrainedMilliseconds,
    double PeakReadyQueue,
    bool TimedOut);

internal sealed record BenchmarkSettings(
    string SlimFaasUrl,
    string DirectUrl,
    IReadOnlyList<string> NodeUrls,
    string Function,
    string ScaleFunction,
    int DurationSeconds,
    int WarmupSeconds,
    int Repetitions,
    IReadOnlyList<int> PayloadBytes,
    IReadOnlyList<int> Concurrency,
    int ScaleMessages,
    int ScaleConcurrency,
    int ScaleTargetReplicas);

internal sealed record BenchmarkReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string OperatingSystem,
    string Framework,
    int ProcessorCount,
    BenchmarkSettings Settings,
    IReadOnlyList<LatencyRunResult> LatencyRuns,
    IReadOnlyList<LatencyAggregateResult> Latency,
    IReadOnlyList<SyncOverheadResult> SyncOverhead,
    ScaleResult Scaling);

internal sealed record FunctionStatusDto(
    int NumberReady,
    int NumberRequested,
    string PodType,
    string Visibility,
    string Name);

internal sealed record QueueDepth(double? Ready, double? InFlight, double? Retry)
{
    public double Total => (Ready ?? 0) + (InFlight ?? 0) + (Retry ?? 0);
    public bool HasValue => Ready.HasValue || InFlight.HasValue || Retry.HasValue;
}

internal static class Statistics
{
    public static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;
        double rank = percentile * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return sorted[lower];
        double fraction = rank - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    public static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return Percentile(sorted, 0.50);
    }

    public static double PercentDelta(double baseline, double candidate) =>
        baseline <= 0 ? 0 : ((candidate - baseline) / baseline) * 100d;
}
