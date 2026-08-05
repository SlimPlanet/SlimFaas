namespace SlimFaasBenchmark;

internal readonly record struct RequestSample(
    string Mode,
    int PayloadBytes,
    int Concurrency,
    int Repetition,
    bool Success,
    double LatencyMilliseconds,
<<<<<<< HEAD
    string? Error,
    string? RequestId = null);

internal sealed record AsyncResourceUsage(
    double? CpuMillisecondsPerMessage,
    double? RaftEntriesPerMessage,
    double? PeakWorkingSetBytes,
    double? CpuSeconds,
    double? RaftEntries);
=======
    string? Error);
>>>>>>> origin/main

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
<<<<<<< HEAD
    TargetObservationSummary? AsyncDelivery,
    double MeanMilliseconds = 0,
    int AsyncMissing = 0,
    int AsyncDuplicates = 0,
    AsyncResourceUsage? AsyncResources = null,
    string LoadShape = "steady");
=======
    TargetObservationSummary? AsyncDelivery);
>>>>>>> origin/main

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
<<<<<<< HEAD
    int? AsyncObserved,
    double MeanMilliseconds = 0,
    double? AsyncArrivalMeanMilliseconds = null,
    double? AsyncCompletionMeanMilliseconds = null,
    int AsyncMissing = 0,
    int AsyncDuplicates = 0,
    double? AsyncCpuMillisecondsPerMessage = null,
    double? AsyncRaftEntriesPerMessage = null,
    double? AsyncPeakWorkingSetBytes = null,
    string LoadShape = "steady");

internal sealed record AsyncWorkloadResult(
    string LoadShape,
    int PayloadBytes,
    int Concurrency,
    int Repetition,
    int Requested,
    int Accepted,
    int Failed,
    int Missing,
    int Duplicates,
    double ElapsedSeconds,
    double RatePerSecond,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    TargetObservationSummary Delivery,
    AsyncResourceUsage? Resources);
=======
    int? AsyncObserved);
>>>>>>> origin/main

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
<<<<<<< HEAD
    int ScaleTargetReplicas,
    string Profile = "standard",
    int AsyncPacedMessages = 0,
    int AsyncPacedIntervalMilliseconds = 0,
    int AsyncBurstMessages = 0,
    int AsyncBurstConcurrency = 0);
=======
    int ScaleTargetReplicas);
>>>>>>> origin/main

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
<<<<<<< HEAD
    ScaleResult Scaling,
    IReadOnlyList<AsyncWorkloadResult>? AsyncWorkloads = null);
=======
    ScaleResult Scaling);
>>>>>>> origin/main

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
