using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SlimFaasBenchmark;

internal sealed record SyncComparisonRow(
    int PayloadBytes,
    int Concurrency,
    double BaselineDirectP50Milliseconds,
    double CandidateDirectP50Milliseconds,
    double BaselineSlimFaasP50Milliseconds,
    double CandidateSlimFaasP50Milliseconds,
    double BaselineAddedP50Milliseconds,
    double CandidateAddedP50Milliseconds,
    double AddedP50GainMilliseconds,
    double AddedP50ReductionPercent,
    double BaselineAddedP95Milliseconds,
    double CandidateAddedP95Milliseconds,
    double AddedP95ChangePercent,
    double BaselineAddedP99Milliseconds,
    double CandidateAddedP99Milliseconds,
    double AddedP99ChangePercent,
    double BaselineRateRatio,
    double CandidateRateRatio,
    double RateRatioChangePercent,
    double BaselineSlimFaasRatePerSecond,
    double CandidateSlimFaasRatePerSecond,
    bool GuardrailsPassed,
    string Verdict);

internal sealed record AsyncComparisonRow(
    int PayloadBytes,
    int Concurrency,
<<<<<<< HEAD
    double BaselineHttpMeanMilliseconds,
    double CandidateHttpMeanMilliseconds,
    double BaselineHttpP50Milliseconds,
    double CandidateHttpP50Milliseconds,
    double BaselineHttpP95Milliseconds,
    double CandidateHttpP95Milliseconds,
    double BaselineHttpP99Milliseconds,
    double CandidateHttpP99Milliseconds,
    double? BaselineArrivalMeanMilliseconds,
    double? CandidateArrivalMeanMilliseconds,
    double? BaselineArrivalP50Milliseconds,
    double? CandidateArrivalP50Milliseconds,
    double? BaselineArrivalP95Milliseconds,
    double? CandidateArrivalP95Milliseconds,
    double? BaselineArrivalP99Milliseconds,
    double? CandidateArrivalP99Milliseconds,
    double HttpP50ReductionPercent,
    double HttpP95ReductionPercent,
    double? ArrivalP50ReductionPercent,
    double? ArrivalP95ReductionPercent,
    double HttpP99ChangePercent,
    double? ArrivalP99ChangePercent,
    double BaselineRatePerSecond,
    double CandidateRatePerSecond,
    double RateChangePercent,
    double? BaselineCpuMillisecondsPerMessage,
    double? CandidateCpuMillisecondsPerMessage,
    double? BaselineRaftEntriesPerMessage,
    double? CandidateRaftEntriesPerMessage,
    double? BaselinePeakWorkingSetBytes,
    double? CandidatePeakWorkingSetBytes,
    long CandidateFailed,
    int CandidateMissing,
    int CandidateDuplicates,
    bool GuardrailsPassed,
    string Verdict);

internal sealed record AsyncWorkloadComparisonRow(
    string LoadShape,
    int PayloadBytes,
    int Concurrency,
    double BaselineRatePerSecond,
    double CandidateRatePerSecond,
    double RateChangePercent,
    double BaselineHttpP95Milliseconds,
    double CandidateHttpP95Milliseconds,
    double BaselineArrivalP95Milliseconds,
    double CandidateArrivalP95Milliseconds,
    int CandidateFailed,
    int CandidateMissing,
    int CandidateDuplicates,
    string Verdict);

internal sealed record AsyncAcceptanceCriteria(
    double? MedianHttpP95ReductionPercent,
    double? MedianArrivalP95ReductionPercent,
    double? LargePayloadMedianHttpP50ReductionPercent,
    double? CpuChangePercent,
    double? MemoryChangePercent,
    double? RaftEntryRatio,
    bool HttpP95TargetPassed,
    bool ArrivalP95TargetPassed,
    bool LargePayloadTargetPassed,
    bool ResourceGuardrailsPassed,
    bool TailAndThroughputGuardrailsPassed,
    bool WorkloadGuardrailsPassed);

=======
    double BaselineHttpP50Milliseconds,
    double CandidateHttpP50Milliseconds,
    double? BaselineArrivalP50Milliseconds,
    double? CandidateArrivalP50Milliseconds,
    long CandidateFailed,
    string Verdict);

>>>>>>> origin/main
internal sealed record ScalingComparison(
    double? BaselineReadyOneMilliseconds,
    double? CandidateReadyOneMilliseconds,
    double? BaselineReadyTargetMilliseconds,
    double? CandidateReadyTargetMilliseconds,
    double? BaselineQueueDrainedMilliseconds,
    double? CandidateQueueDrainedMilliseconds,
    int CandidateFailed,
    bool CandidateTimedOut,
    string Verdict);

internal sealed record BenchmarkComparisonReport(
    DateTimeOffset GeneratedAtUtc,
    string BaselinePath,
    string CandidatePath,
    double? SmallPayloadMedianP50ReductionPercent,
    double? LargePayloadMedianP50ReductionPercent,
    bool SmallPayloadTargetPassed,
    bool LargePayloadTargetPassed,
    bool SyncGuardrailsPassed,
    bool CandidateHasNoFailures,
    bool Passed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<SyncComparisonRow> Sync,
    IReadOnlyList<AsyncComparisonRow> Async,
<<<<<<< HEAD
    ScalingComparison Scaling,
    string Profile = "sync",
    AsyncAcceptanceCriteria? AsyncCriteria = null,
    IReadOnlyList<AsyncWorkloadComparisonRow>? AsyncWorkloads = null);
=======
    ScalingComparison Scaling);
>>>>>>> origin/main

internal static class BenchmarkComparer
{
    private const double SmallPayloadMinimumReductionPercent = 20d;
    private const double LargePayloadMinimumReductionPercent = 40d;
    private const double MaximumGuardrailRegressionPercent = 10d;
<<<<<<< HEAD
    private const double AsyncHttpP95MinimumReductionPercent = 50d;
    private const double AsyncArrivalP95MinimumReductionPercent = 40d;
    private const double AsyncLargePayloadP50MinimumReductionPercent = 40d;
    private const double MaximumCpuRegressionPercent = 20d;
    private const double MaximumMemoryRegressionPercent = 10d;
    private const double MaximumRaftEntryRatio = 2d;
=======
>>>>>>> origin/main

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(CommandArguments arguments)
    {
        string baselinePath = RequiredPath(arguments, "baseline");
        string candidatePath = RequiredPath(arguments, "candidate");
        string outputValue = arguments.Get(
            "output",
            Path.Combine(Path.GetDirectoryName(candidatePath) ?? ".", "comparison"));
        string outputDirectory = ResolvePath(outputValue, mustExist: false);
<<<<<<< HEAD
        string profile = arguments.Get("profile", "sync").ToLowerInvariant();
        if (profile is not ("sync" or "async"))
            throw new ArgumentException("--profile must be 'sync' or 'async'.");
=======
>>>>>>> origin/main

        BenchmarkReport baseline = await ReadReportAsync(baselinePath);
        BenchmarkReport candidate = await ReadReportAsync(candidatePath);
        ValidateCompatibleSettings(baseline.Settings, candidate.Settings);

        BenchmarkComparisonReport comparison = BuildComparison(
            Path.GetFullPath(baselinePath),
            Path.GetFullPath(candidatePath),
            baseline,
<<<<<<< HEAD
            candidate,
            profile);
=======
            candidate);
>>>>>>> origin/main

        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "comparison.json"),
            JsonSerializer.Serialize(comparison, JsonOptions));
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "comparison.md"),
            BuildMarkdown(comparison));

        Console.WriteLine(comparison.Passed
<<<<<<< HEAD
            ? $"PASS: the {profile} optimization met every comparison criterion."
            : $"FAIL: one or more {profile} optimization criteria were not met.");
=======
            ? "PASS: the sync optimization met every comparison criterion."
            : "FAIL: one or more sync optimization criteria were not met.");
>>>>>>> origin/main
        Console.WriteLine($"Comparison artifacts: {outputDirectory}");
        return comparison.Passed ? 0 : 1;
    }

    internal static BenchmarkComparisonReport BuildComparison(
        string baselinePath,
        string candidatePath,
        BenchmarkReport baseline,
<<<<<<< HEAD
        BenchmarkReport candidate,
        string profile = "sync")
    {
        var failures = new List<string>();
        bool asyncProfile = string.Equals(profile, "async", StringComparison.OrdinalIgnoreCase);
=======
        BenchmarkReport candidate)
    {
        var failures = new List<string>();
>>>>>>> origin/main
        SyncComparisonRow[] syncRows = baseline.SyncOverhead
            .OrderBy(row => row.PayloadBytes)
            .ThenBy(row => row.Concurrency)
            .Select(baselineRow =>
            {
                SyncOverheadResult candidateRow = candidate.SyncOverhead.Single(row =>
                    row.PayloadBytes == baselineRow.PayloadBytes &&
                    row.Concurrency == baselineRow.Concurrency);
                double p50Reduction = ReductionPercent(
                    baselineRow.AddedP50Milliseconds,
                    candidateRow.AddedP50Milliseconds);
                double p95Change = ChangePercent(
                    baselineRow.AddedP95Milliseconds,
                    candidateRow.AddedP95Milliseconds);
                double p99Change = ChangePercent(
                    baselineRow.AddedP99Milliseconds,
                    candidateRow.AddedP99Milliseconds);
                double rateChange = ChangePercent(
                    baselineRow.RateRatio,
                    candidateRow.RateRatio);
                bool guardrailsPassed =
                    p95Change <= MaximumGuardrailRegressionPercent &&
                    p99Change <= MaximumGuardrailRegressionPercent &&
                    rateChange >= -MaximumGuardrailRegressionPercent;
                LatencyAggregateResult candidateLatency = candidate.Latency.Single(row =>
                    row.Mode == "sync" &&
                    row.PayloadBytes == baselineRow.PayloadBytes &&
                    row.Concurrency == baselineRow.Concurrency);
<<<<<<< HEAD
                string verdict = asyncProfile
                    ? "INFO"
                    : guardrailsPassed && candidateLatency.Failed == 0 ? "PASS" : "FAIL";
=======
                string verdict = guardrailsPassed && candidateLatency.Failed == 0
                    ? "PASS"
                    : "FAIL";
>>>>>>> origin/main

                return new SyncComparisonRow(
                    baselineRow.PayloadBytes,
                    baselineRow.Concurrency,
                    baselineRow.DirectP50Milliseconds,
                    candidateRow.DirectP50Milliseconds,
                    baselineRow.SlimFaasP50Milliseconds,
                    candidateRow.SlimFaasP50Milliseconds,
                    baselineRow.AddedP50Milliseconds,
                    candidateRow.AddedP50Milliseconds,
                    baselineRow.AddedP50Milliseconds - candidateRow.AddedP50Milliseconds,
                    p50Reduction,
                    baselineRow.AddedP95Milliseconds,
                    candidateRow.AddedP95Milliseconds,
                    p95Change,
                    baselineRow.AddedP99Milliseconds,
                    candidateRow.AddedP99Milliseconds,
                    p99Change,
                    baselineRow.RateRatio,
                    candidateRow.RateRatio,
                    rateChange,
                    baselineRow.SlimFaasRatePerSecond,
                    candidateRow.SlimFaasRatePerSecond,
                    guardrailsPassed,
                    verdict);
            })
            .ToArray();

        double? smallReduction = MedianOrNull(syncRows
            .Where(row => row.PayloadBytes <= 4 * 1024)
            .Select(row => row.AddedP50ReductionPercent));
        double? largeReduction = MedianOrNull(syncRows
            .Where(row => row.PayloadBytes >= 256 * 1024)
            .Select(row => row.AddedP50ReductionPercent));
        bool smallPassed = smallReduction is null ||
                           smallReduction >= SmallPayloadMinimumReductionPercent;
        bool largePassed = largeReduction is null ||
                           largeReduction >= LargePayloadMinimumReductionPercent;
        bool guardrailsPassed = syncRows.All(row => row.GuardrailsPassed);

<<<<<<< HEAD
        if (!asyncProfile && !smallPassed)
            failures.Add(FormattableString.Invariant(
                $"Small-payload median p50 reduction was {smallReduction:F1}%, below {SmallPayloadMinimumReductionPercent:F1}%."));
        if (!asyncProfile && !largePassed)
            failures.Add(FormattableString.Invariant(
                $"Large-payload median p50 reduction was {largeReduction:F1}%, below {LargePayloadMinimumReductionPercent:F1}%."));
        foreach (SyncComparisonRow row in syncRows.Where(row => !asyncProfile && !row.GuardrailsPassed))
=======
        if (!smallPassed)
            failures.Add(FormattableString.Invariant(
                $"Small-payload median p50 reduction was {smallReduction:F1}%, below {SmallPayloadMinimumReductionPercent:F1}%."));
        if (!largePassed)
            failures.Add(FormattableString.Invariant(
                $"Large-payload median p50 reduction was {largeReduction:F1}%, below {LargePayloadMinimumReductionPercent:F1}%."));
        foreach (SyncComparisonRow row in syncRows.Where(row => !row.GuardrailsPassed))
>>>>>>> origin/main
        {
            failures.Add(
                $"Guardrail failed for {FormatBytes(row.PayloadBytes)} at concurrency {row.Concurrency}.");
        }

        AsyncComparisonRow[] asyncRows = baseline.Latency
            .Where(row => row.Mode == "async")
            .OrderBy(row => row.PayloadBytes)
            .ThenBy(row => row.Concurrency)
            .Select(baselineRow =>
            {
                LatencyAggregateResult candidateRow = candidate.Latency.Single(row =>
                    row.Mode == "async" &&
                    row.PayloadBytes == baselineRow.PayloadBytes &&
                    row.Concurrency == baselineRow.Concurrency);
<<<<<<< HEAD
                double httpP50Reduction = ReductionPercent(
                    baselineRow.P50Milliseconds, candidateRow.P50Milliseconds);
                double httpP95Reduction = ReductionPercent(
                    baselineRow.P95Milliseconds, candidateRow.P95Milliseconds);
                double? arrivalP50Reduction = ReductionPercentNullable(
                    baselineRow.AsyncArrivalP50Milliseconds, candidateRow.AsyncArrivalP50Milliseconds);
                double? arrivalP95Reduction = ReductionPercentNullable(
                    baselineRow.AsyncArrivalP95Milliseconds, candidateRow.AsyncArrivalP95Milliseconds);
                double httpP99Change = ChangePercent(
                    baselineRow.P99Milliseconds, candidateRow.P99Milliseconds);
                double? arrivalP99Change = ChangePercentNullable(
                    baselineRow.AsyncArrivalP99Milliseconds, candidateRow.AsyncArrivalP99Milliseconds);
                double rateChange = ChangePercent(
                    baselineRow.MeanRatePerSecond, candidateRow.MeanRatePerSecond);
                bool rowGuardrails = httpP99Change <= MaximumGuardrailRegressionPercent &&
                                     (!arrivalP99Change.HasValue ||
                                      arrivalP99Change <= MaximumGuardrailRegressionPercent) &&
                                     rateChange >= -MaximumGuardrailRegressionPercent;
                bool correct = candidateRow.Failed == 0 &&
                               candidateRow.AsyncMissing == 0 &&
                               candidateRow.AsyncDuplicates == 0;
                return new AsyncComparisonRow(
                    baselineRow.PayloadBytes,
                    baselineRow.Concurrency,
                    baselineRow.MeanMilliseconds,
                    candidateRow.MeanMilliseconds,
                    baselineRow.P50Milliseconds,
                    candidateRow.P50Milliseconds,
                    baselineRow.P95Milliseconds,
                    candidateRow.P95Milliseconds,
                    baselineRow.P99Milliseconds,
                    candidateRow.P99Milliseconds,
                    baselineRow.AsyncArrivalMeanMilliseconds,
                    candidateRow.AsyncArrivalMeanMilliseconds,
                    baselineRow.AsyncArrivalP50Milliseconds,
                    candidateRow.AsyncArrivalP50Milliseconds,
                    baselineRow.AsyncArrivalP95Milliseconds,
                    candidateRow.AsyncArrivalP95Milliseconds,
                    baselineRow.AsyncArrivalP99Milliseconds,
                    candidateRow.AsyncArrivalP99Milliseconds,
                    httpP50Reduction,
                    httpP95Reduction,
                    arrivalP50Reduction,
                    arrivalP95Reduction,
                    httpP99Change,
                    arrivalP99Change,
                    baselineRow.MeanRatePerSecond,
                    candidateRow.MeanRatePerSecond,
                    rateChange,
                    baselineRow.AsyncCpuMillisecondsPerMessage,
                    candidateRow.AsyncCpuMillisecondsPerMessage,
                    baselineRow.AsyncRaftEntriesPerMessage,
                    candidateRow.AsyncRaftEntriesPerMessage,
                    baselineRow.AsyncPeakWorkingSetBytes,
                    candidateRow.AsyncPeakWorkingSetBytes,
                    candidateRow.Failed,
                    candidateRow.AsyncMissing,
                    candidateRow.AsyncDuplicates,
                    rowGuardrails,
                    correct && rowGuardrails ? "PASS" : "FAIL");
            })
            .ToArray();

        AsyncWorkloadComparisonRow[] asyncWorkloads = BuildAsyncWorkloadComparison(
            baseline.AsyncWorkloads ?? [], candidate.AsyncWorkloads ?? []);

        double? asyncHttpP95Reduction = MedianOrNull(asyncRows.Select(row => row.HttpP95ReductionPercent));
        double? asyncArrivalP95Reduction = MedianOrNull(asyncRows
            .Where(row => row.ArrivalP95ReductionPercent.HasValue)
            .Select(row => row.ArrivalP95ReductionPercent!.Value));
        double? asyncLargeP50Reduction = MedianOrNull(asyncRows
            .Where(row => row.PayloadBytes >= 2 * 1024 * 1024)
            .Select(row => row.HttpP50ReductionPercent));
        double? cpuChange = ResourceChangePercent(
            asyncRows.Select(row => row.BaselineCpuMillisecondsPerMessage),
            asyncRows.Select(row => row.CandidateCpuMillisecondsPerMessage));
        double? memoryChange = ResourceChangePercent(
            asyncRows.Select(row => row.BaselinePeakWorkingSetBytes),
            asyncRows.Select(row => row.CandidatePeakWorkingSetBytes));
        double? raftRatio = ResourceRatio(
            asyncRows.Select(row => row.BaselineRaftEntriesPerMessage),
            asyncRows.Select(row => row.CandidateRaftEntriesPerMessage));
        bool httpP95Passed = asyncHttpP95Reduction is not null &&
                             asyncHttpP95Reduction >= AsyncHttpP95MinimumReductionPercent;
        bool arrivalP95Passed = asyncArrivalP95Reduction is not null &&
                                asyncArrivalP95Reduction >= AsyncArrivalP95MinimumReductionPercent;
        bool asyncLargePassed = asyncLargeP50Reduction is not null &&
                                asyncLargeP50Reduction >= AsyncLargePayloadP50MinimumReductionPercent;
        bool resourcePassed = (!cpuChange.HasValue || cpuChange <= MaximumCpuRegressionPercent) &&
                              (!memoryChange.HasValue || memoryChange <= MaximumMemoryRegressionPercent) &&
                              (!raftRatio.HasValue || raftRatio <= MaximumRaftEntryRatio);
        bool asyncGuardrailsPassed = asyncRows.All(row => row.GuardrailsPassed);
        bool workloadGuardrailsPassed = asyncWorkloads.All(row => row.Verdict == "PASS");
        var asyncCriteria = new AsyncAcceptanceCriteria(
            asyncHttpP95Reduction,
            asyncArrivalP95Reduction,
            asyncLargeP50Reduction,
            cpuChange,
            memoryChange,
            raftRatio,
            httpP95Passed,
            arrivalP95Passed,
            asyncLargePassed,
            resourcePassed,
            asyncGuardrailsPassed,
            workloadGuardrailsPassed);

=======
                return new AsyncComparisonRow(
                    baselineRow.PayloadBytes,
                    baselineRow.Concurrency,
                    baselineRow.P50Milliseconds,
                    candidateRow.P50Milliseconds,
                    baselineRow.AsyncArrivalP50Milliseconds,
                    candidateRow.AsyncArrivalP50Milliseconds,
                    candidateRow.Failed,
                    candidateRow.Failed == 0 ? "PASS" : "FAIL");
            })
            .ToArray();

>>>>>>> origin/main
        var scaling = new ScalingComparison(
            baseline.Scaling.ReadyOneMilliseconds,
            candidate.Scaling.ReadyOneMilliseconds,
            baseline.Scaling.ReadyTargetMilliseconds,
            candidate.Scaling.ReadyTargetMilliseconds,
            baseline.Scaling.QueueDrainedMilliseconds,
            candidate.Scaling.QueueDrainedMilliseconds,
            candidate.Scaling.Failed,
            candidate.Scaling.TimedOut,
            candidate.Scaling.Failed == 0 && !candidate.Scaling.TimedOut ? "PASS" : "FAIL");

        long candidateLatencyFailures = candidate.Latency.Sum(row => row.Failed);
<<<<<<< HEAD
        int candidateMissing = candidate.Latency.Sum(row => row.AsyncMissing) +
                               (candidate.AsyncWorkloads?.Sum(row => row.Missing) ?? 0);
        int candidateDuplicates = candidate.Latency.Sum(row => row.AsyncDuplicates) +
                                  (candidate.AsyncWorkloads?.Sum(row => row.Duplicates) ?? 0);
        int candidateWorkloadFailures = candidate.AsyncWorkloads?.Sum(row => row.Failed) ?? 0;
        bool noFailures = candidateLatencyFailures == 0 &&
                          candidateWorkloadFailures == 0 &&
                          candidateMissing == 0 &&
                          candidateDuplicates == 0 &&
=======
        bool noFailures = candidateLatencyFailures == 0 &&
>>>>>>> origin/main
                          candidate.Scaling.Failed == 0 &&
                          !candidate.Scaling.TimedOut;
        if (!noFailures)
        {
            failures.Add(
<<<<<<< HEAD
                $"Candidate recorded {candidateLatencyFailures + candidateWorkloadFailures} request failures, " +
                $"{candidateMissing} missing, {candidateDuplicates} duplicates, " +
                $"{candidate.Scaling.Failed} scaling failures, timedOut={candidate.Scaling.TimedOut}.");
        }

        if (asyncProfile)
        {
            AddAsyncCriteriaFailures(failures, asyncCriteria);
            foreach (AsyncComparisonRow row in asyncRows.Where(row => !row.GuardrailsPassed))
                failures.Add($"Async p99/throughput guardrail failed for {FormatBytes(row.PayloadBytes)} at concurrency {row.Concurrency}.");
            foreach (AsyncWorkloadComparisonRow row in asyncWorkloads.Where(row => row.Verdict != "PASS"))
                failures.Add($"Async {row.LoadShape} workload guardrail failed at concurrency {row.Concurrency}.");
        }

        bool performancePassed = asyncProfile
            ? httpP95Passed && arrivalP95Passed && asyncLargePassed && resourcePassed &&
              asyncGuardrailsPassed && workloadGuardrailsPassed
            : smallPassed && largePassed && guardrailsPassed;

=======
                $"Candidate recorded {candidateLatencyFailures} latency failures, " +
                $"{candidate.Scaling.Failed} scaling failures, timedOut={candidate.Scaling.TimedOut}.");
        }

>>>>>>> origin/main
        return new BenchmarkComparisonReport(
            DateTimeOffset.UtcNow,
            baselinePath,
            candidatePath,
            smallReduction,
            largeReduction,
            smallPassed,
            largePassed,
            guardrailsPassed,
            noFailures,
<<<<<<< HEAD
            performancePassed && noFailures,
            failures,
            syncRows,
            asyncRows,
            scaling,
            asyncProfile ? "async" : "sync",
            asyncCriteria,
            asyncWorkloads);
    }

    internal static async Task<BenchmarkReport> ReadReportAsync(string path)
=======
            smallPassed && largePassed && guardrailsPassed && noFailures,
            failures,
            syncRows,
            asyncRows,
            scaling);
    }

    private static async Task<BenchmarkReport> ReadReportAsync(string path)
>>>>>>> origin/main
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Benchmark results file not found.", path);
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BenchmarkReport>(stream, JsonOptions)
               ?? throw new InvalidDataException($"Unable to deserialize benchmark report '{path}'.");
    }

    private static void ValidateCompatibleSettings(BenchmarkSettings baseline, BenchmarkSettings candidate)
    {
        bool compatible = baseline.Function == candidate.Function &&
                          baseline.ScaleFunction == candidate.ScaleFunction &&
                          baseline.DurationSeconds == candidate.DurationSeconds &&
                          baseline.WarmupSeconds == candidate.WarmupSeconds &&
                          baseline.Repetitions == candidate.Repetitions &&
                          baseline.ScaleMessages == candidate.ScaleMessages &&
                          baseline.ScaleConcurrency == candidate.ScaleConcurrency &&
                          baseline.ScaleTargetReplicas == candidate.ScaleTargetReplicas &&
<<<<<<< HEAD
                          baseline.Profile == candidate.Profile &&
                          baseline.AsyncPacedMessages == candidate.AsyncPacedMessages &&
                          baseline.AsyncPacedIntervalMilliseconds == candidate.AsyncPacedIntervalMilliseconds &&
                          baseline.AsyncBurstMessages == candidate.AsyncBurstMessages &&
                          baseline.AsyncBurstConcurrency == candidate.AsyncBurstConcurrency &&
=======
>>>>>>> origin/main
                          baseline.PayloadBytes.SequenceEqual(candidate.PayloadBytes) &&
                          baseline.Concurrency.SequenceEqual(candidate.Concurrency);
        if (!compatible)
        {
            throw new InvalidOperationException(
                "Baseline and candidate settings differ. Run both benchmarks with the exact same matrix.");
        }
    }

    private static string RequiredPath(CommandArguments arguments, string name)
    {
        string value = arguments.GetOptional(name) ??
                       throw new ArgumentException($"--{name} is required.");
        return ResolvePath(value, mustExist: true);
    }

    private static string ResolvePath(string value, bool mustExist)
    {
        string currentPath = Path.GetFullPath(value);
        if (Path.IsPathFullyQualified(value) || (mustExist && File.Exists(currentPath)))
            return currentPath;

        string? repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is not null)
        {
            string repositoryPath = Path.GetFullPath(value, repositoryRoot);
            if (!mustExist || File.Exists(repositoryPath))
                return repositoryPath;
        }

        return currentPath;
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SlimFaas.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static double ReductionPercent(double baseline, double candidate) =>
        baseline <= 0d ? (candidate <= baseline ? 0d : -100d) :
        ((baseline - candidate) / baseline) * 100d;

    private static double ChangePercent(double baseline, double candidate) =>
        baseline <= 0d ? (candidate <= baseline ? 0d : 100d) :
        ((candidate - baseline) / baseline) * 100d;

    private static double? MedianOrNull(IEnumerable<double> values)
    {
        double[] snapshot = values.ToArray();
        return snapshot.Length == 0 ? null : Statistics.Median(snapshot);
    }

<<<<<<< HEAD
    private static double? ReductionPercentNullable(double? baseline, double? candidate) =>
        baseline.HasValue && candidate.HasValue
            ? ReductionPercent(baseline.Value, candidate.Value)
            : null;

    private static double? ChangePercentNullable(double? baseline, double? candidate) =>
        baseline.HasValue && candidate.HasValue
            ? ChangePercent(baseline.Value, candidate.Value)
            : null;

    private static double? ResourceChangePercent(
        IEnumerable<double?> baseline,
        IEnumerable<double?> candidate)
    {
        double? baselineMedian = MedianNullable(baseline);
        double? candidateMedian = MedianNullable(candidate);
        return baselineMedian.HasValue && candidateMedian.HasValue
            ? ChangePercent(baselineMedian.Value, candidateMedian.Value)
            : null;
    }

    private static double? ResourceRatio(
        IEnumerable<double?> baseline,
        IEnumerable<double?> candidate)
    {
        double? baselineMedian = MedianNullable(baseline);
        double? candidateMedian = MedianNullable(candidate);
        if (!baselineMedian.HasValue || !candidateMedian.HasValue || baselineMedian <= 0)
            return null;
        return candidateMedian.Value / baselineMedian.Value;
    }

    private static double? MedianNullable(IEnumerable<double?> values)
    {
        double[] snapshot = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return snapshot.Length == 0 ? null : Statistics.Median(snapshot);
    }

    private static AsyncWorkloadComparisonRow[] BuildAsyncWorkloadComparison(
        IReadOnlyList<AsyncWorkloadResult> baseline,
        IReadOnlyList<AsyncWorkloadResult> candidate)
    {
        if (baseline.Count == 0 && candidate.Count == 0)
            return [];

        return baseline
            .GroupBy(row => new { row.LoadShape, row.PayloadBytes, row.Concurrency })
            .OrderBy(group => group.Key.LoadShape, StringComparer.Ordinal)
            .ThenBy(group => group.Key.PayloadBytes)
            .ThenBy(group => group.Key.Concurrency)
            .Select(group =>
            {
                AsyncWorkloadResult[] baselineRows = group.ToArray();
                AsyncWorkloadResult[] candidateRows = candidate.Where(row =>
                    row.LoadShape == group.Key.LoadShape &&
                    row.PayloadBytes == group.Key.PayloadBytes &&
                    row.Concurrency == group.Key.Concurrency).ToArray();
                if (candidateRows.Length == 0)
                    throw new InvalidOperationException($"Candidate is missing async workload '{group.Key.LoadShape}'.");

                double baselineRate = Statistics.Median(baselineRows.Select(row => row.RatePerSecond));
                double candidateRate = Statistics.Median(candidateRows.Select(row => row.RatePerSecond));
                double baselineHttpP95 = Statistics.Median(baselineRows.Select(row => row.P95Milliseconds));
                double candidateHttpP95 = Statistics.Median(candidateRows.Select(row => row.P95Milliseconds));
                double baselineArrivalP95 = Statistics.Median(
                    baselineRows.Select(row => row.Delivery.ArrivalP95Milliseconds));
                double candidateArrivalP95 = Statistics.Median(
                    candidateRows.Select(row => row.Delivery.ArrivalP95Milliseconds));
                int failed = candidateRows.Sum(row => row.Failed);
                int missing = candidateRows.Sum(row => row.Missing);
                int duplicates = candidateRows.Sum(row => row.Duplicates);
                double rateChange = ChangePercent(baselineRate, candidateRate);
                bool guardrails = rateChange >= -MaximumGuardrailRegressionPercent &&
                                  ChangePercent(baselineHttpP95, candidateHttpP95) <=
                                  MaximumGuardrailRegressionPercent &&
                                  ChangePercent(baselineArrivalP95, candidateArrivalP95) <=
                                  MaximumGuardrailRegressionPercent;
                return new AsyncWorkloadComparisonRow(
                    group.Key.LoadShape,
                    group.Key.PayloadBytes,
                    group.Key.Concurrency,
                    baselineRate,
                    candidateRate,
                    rateChange,
                    baselineHttpP95,
                    candidateHttpP95,
                    baselineArrivalP95,
                    candidateArrivalP95,
                    failed,
                    missing,
                    duplicates,
                    guardrails && failed == 0 && missing == 0 && duplicates == 0 ? "PASS" : "FAIL");
            })
            .ToArray();
    }

    private static void AddAsyncCriteriaFailures(
        ICollection<string> failures,
        AsyncAcceptanceCriteria criteria)
    {
        if (!criteria.HttpP95TargetPassed)
            failures.Add($"Async median HTTP p95 reduction was {criteria.MedianHttpP95ReductionPercent:F1}%, below {AsyncHttpP95MinimumReductionPercent:F1}%.");
        if (!criteria.ArrivalP95TargetPassed)
            failures.Add($"Async median arrival p95 reduction was {criteria.MedianArrivalP95ReductionPercent:F1}%, below {AsyncArrivalP95MinimumReductionPercent:F1}%.");
        if (!criteria.LargePayloadTargetPassed)
            failures.Add($"Async 2 MiB median HTTP p50 reduction was {criteria.LargePayloadMedianHttpP50ReductionPercent:F1}%, below {AsyncLargePayloadP50MinimumReductionPercent:F1}%.");
        if (!criteria.ResourceGuardrailsPassed)
            failures.Add(
                $"Async resource guardrail failed (CPU={criteria.CpuChangePercent:F1}%, " +
                $"memory={criteria.MemoryChangePercent:F1}%, Raft ratio={criteria.RaftEntryRatio:F2}).");
    }

    private static string BuildMarkdown(BenchmarkComparisonReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# SlimFaas {report.Profile} optimization comparison");
=======
    private static string BuildMarkdown(BenchmarkComparisonReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SlimFaas sync optimization comparison");
>>>>>>> origin/main
        builder.AppendLine();
        builder.AppendLine($"Overall verdict: **{(report.Passed ? "PASS" : "FAIL")}**.");
        builder.AppendLine();
        builder.AppendLine("## Acceptance criteria");
        builder.AppendLine();
        builder.AppendLine("| criterion | observed | target | verdict |");
        builder.AppendLine("|---|---:|---:|:---:|");
<<<<<<< HEAD
        if (report.Profile == "async" && report.AsyncCriteria is { } asyncCriteria)
        {
            AppendCriterion(builder, "Async median HTTP p95 reduction",
                asyncCriteria.MedianHttpP95ReductionPercent, AsyncHttpP95MinimumReductionPercent,
                asyncCriteria.HttpP95TargetPassed);
            AppendCriterion(builder, "Async median arrival p95 reduction",
                asyncCriteria.MedianArrivalP95ReductionPercent, AsyncArrivalP95MinimumReductionPercent,
                asyncCriteria.ArrivalP95TargetPassed);
            AppendCriterion(builder, "Async 2 MiB HTTP p50 reduction",
                asyncCriteria.LargePayloadMedianHttpP50ReductionPercent,
                AsyncLargePayloadP50MinimumReductionPercent,
                asyncCriteria.LargePayloadTargetPassed);
            builder.AppendLine($"| async p99 and throughput guardrails | — | max {MaximumGuardrailRegressionPercent:F0}% regression | {(asyncCriteria.TailAndThroughputGuardrailsPassed ? "PASS" : "FAIL")} |");
            builder.AppendLine($"| CPU / memory / Raft guardrails | {FormatResources(asyncCriteria)} | +{MaximumCpuRegressionPercent:F0}% / +{MaximumMemoryRegressionPercent:F0}% / x{MaximumRaftEntryRatio:F0} | {(asyncCriteria.ResourceGuardrailsPassed ? "PASS" : "FAIL")} |");
        }
        else
        {
            AppendCriterion(builder, "Small payload median added-p50 reduction",
                report.SmallPayloadMedianP50ReductionPercent, SmallPayloadMinimumReductionPercent,
                report.SmallPayloadTargetPassed);
            AppendCriterion(builder, "Large payload median added-p50 reduction",
                report.LargePayloadMedianP50ReductionPercent, LargePayloadMinimumReductionPercent,
                report.LargePayloadTargetPassed);
        }
        builder.AppendLine(report.Profile == "async"
            ? $"| sync p95/p99 and throughput guardrails (informational) | — | max {MaximumGuardrailRegressionPercent:F0}% regression | {(report.SyncGuardrailsPassed ? "PASS" : "OBSERVE")} |"
            : $"| sync p95/p99 and throughput guardrails | — | max {MaximumGuardrailRegressionPercent:F0}% regression | {(report.SyncGuardrailsPassed ? "PASS" : "FAIL")} |");
=======
        AppendCriterion(builder, "Small payload median added-p50 reduction",
            report.SmallPayloadMedianP50ReductionPercent, SmallPayloadMinimumReductionPercent,
            report.SmallPayloadTargetPassed);
        AppendCriterion(builder, "Large payload median added-p50 reduction",
            report.LargePayloadMedianP50ReductionPercent, LargePayloadMinimumReductionPercent,
            report.LargePayloadTargetPassed);
        builder.AppendLine($"| p95/p99 and throughput guardrails | — | max {MaximumGuardrailRegressionPercent:F0}% regression | {(report.SyncGuardrailsPassed ? "PASS" : "FAIL")} |");
>>>>>>> origin/main
        builder.AppendLine($"| candidate errors/timeouts | {(report.CandidateHasNoFailures ? "0" : "present")} | 0 | {(report.CandidateHasNoFailures ? "PASS" : "FAIL")} |");

        builder.AppendLine();
        builder.AppendLine("## Synchronous comparison");
        builder.AppendLine();
        builder.AppendLine("| payload | concurrency | baseline total p50 | candidate total p50 | baseline added p50 | candidate added p50 | gain | reduction | baseline throughput | candidate throughput | verdict |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|");
        foreach (SyncComparisonRow row in report.Sync)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| {FormatBytes(row.PayloadBytes)} | {row.Concurrency} | {row.BaselineSlimFaasP50Milliseconds:F3} ms | {row.CandidateSlimFaasP50Milliseconds:F3} ms | {row.BaselineAddedP50Milliseconds:F3} ms | {row.CandidateAddedP50Milliseconds:F3} ms | {row.AddedP50GainMilliseconds:+0.000;-0.000;0.000} ms | {row.AddedP50ReductionPercent:+0.0;-0.0;0.0}% | {row.BaselineSlimFaasRatePerSecond:F1}/s | {row.CandidateSlimFaasRatePerSecond:F1}/s | {row.Verdict} |"));
        }

        builder.AppendLine();
<<<<<<< HEAD
        builder.AppendLine(report.Profile == "async"
            ? "## Asynchronous comparison"
            : "## Asynchronous comparison (informational)");
        builder.AppendLine();
        builder.AppendLine("| payload | concurrency | HTTP mean before | HTTP mean after | HTTP p95 before | HTTP p95 after | arrival mean before | arrival mean after | arrival p95 before | arrival p95 after | rate change | lost/duplicates | verdict |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|");
        foreach (AsyncComparisonRow row in report.Async)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| {FormatBytes(row.PayloadBytes)} | {row.Concurrency} | {row.BaselineHttpMeanMilliseconds:F3} ms | {row.CandidateHttpMeanMilliseconds:F3} ms | {row.BaselineHttpP95Milliseconds:F3} ms | {row.CandidateHttpP95Milliseconds:F3} ms | {FormatNullable(row.BaselineArrivalMeanMilliseconds)} | {FormatNullable(row.CandidateArrivalMeanMilliseconds)} | {FormatNullable(row.BaselineArrivalP95Milliseconds)} | {FormatNullable(row.CandidateArrivalP95Milliseconds)} | {row.RateChangePercent:+0.0;-0.0;0.0}% | {row.CandidateMissing}/{row.CandidateDuplicates} | {row.Verdict} |"));
        }

        if (report.AsyncWorkloads is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("## Paced and burst workloads");
            builder.AppendLine();
            builder.AppendLine("| shape | concurrency | rate before | rate after | HTTP p95 before | HTTP p95 after | arrival p95 before | arrival p95 after | lost/duplicates | verdict |");
            builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|:---:|");
            foreach (AsyncWorkloadComparisonRow row in report.AsyncWorkloads)
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"| {row.LoadShape} | {row.Concurrency} | {row.BaselineRatePerSecond:F1}/s | {row.CandidateRatePerSecond:F1}/s | {row.BaselineHttpP95Milliseconds:F3} ms | {row.CandidateHttpP95Milliseconds:F3} ms | {row.BaselineArrivalP95Milliseconds:F3} ms | {row.CandidateArrivalP95Milliseconds:F3} ms | {row.CandidateMissing}/{row.CandidateDuplicates} | {row.Verdict} |"));
            }
=======
        builder.AppendLine("## Asynchronous comparison (informational)");
        builder.AppendLine();
        builder.AppendLine("| payload | concurrency | baseline HTTP p50 | candidate HTTP p50 | baseline arrival p50 | candidate arrival p50 | candidate failures | verdict |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|:---:|");
        foreach (AsyncComparisonRow row in report.Async)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| {FormatBytes(row.PayloadBytes)} | {row.Concurrency} | {row.BaselineHttpP50Milliseconds:F3} ms | {row.CandidateHttpP50Milliseconds:F3} ms | {FormatNullable(row.BaselineArrivalP50Milliseconds)} | {FormatNullable(row.CandidateArrivalP50Milliseconds)} | {row.CandidateFailed} | {row.Verdict} |"));
>>>>>>> origin/main
        }

        builder.AppendLine();
        builder.AppendLine("## Scaling comparison (informational)");
        builder.AppendLine();
        builder.AppendLine("| milestone | baseline | candidate |");
        builder.AppendLine("|---|---:|---:|");
        builder.AppendLine($"| first ready replica | {FormatNullable(report.Scaling.BaselineReadyOneMilliseconds)} | {FormatNullable(report.Scaling.CandidateReadyOneMilliseconds)} |");
        builder.AppendLine($"| target ready replicas | {FormatNullable(report.Scaling.BaselineReadyTargetMilliseconds)} | {FormatNullable(report.Scaling.CandidateReadyTargetMilliseconds)} |");
        builder.AppendLine($"| queue drained | {FormatNullable(report.Scaling.BaselineQueueDrainedMilliseconds)} | {FormatNullable(report.Scaling.CandidateQueueDrainedMilliseconds)} |");
        builder.AppendLine();
        builder.AppendLine($"Scaling verdict: **{report.Scaling.Verdict}** (failures: {report.Scaling.CandidateFailed}, timed out: {report.Scaling.CandidateTimedOut}).");

        if (report.Failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Failed criteria");
            builder.AppendLine();
            foreach (string failure in report.Failures)
                builder.AppendLine($"- {failure}");
        }

        builder.AppendLine();
        builder.AppendLine($"Baseline: `{report.BaselinePath}`");
        builder.AppendLine();
        builder.AppendLine($"Candidate: `{report.CandidatePath}`");
        return builder.ToString();
    }

    private static void AppendCriterion(
        StringBuilder builder,
        string label,
        double? observed,
        double target,
        bool passed)
    {
        string observedText = observed.HasValue
            ? observed.Value.ToString("F1", CultureInfo.InvariantCulture) + "%"
            : "not applicable";
        builder.AppendLine(FormattableString.Invariant(
            $"| {label} | {observedText} | >= {target:F0}% | {(passed ? "PASS" : "FAIL")} |"));
    }

    private static string FormatBytes(int bytes) => bytes switch
    {
        >= 1024 * 1024 when bytes % (1024 * 1024) == 0 => $"{bytes / (1024 * 1024)} MiB",
        >= 1024 when bytes % 1024 == 0 => $"{bytes / 1024} KiB",
        _ => $"{bytes} B"
    };

    private static string FormatNullable(double? value) =>
        value.HasValue
            ? value.Value.ToString("F3", CultureInfo.InvariantCulture) + " ms"
            : "not observed";
<<<<<<< HEAD

    private static string FormatResources(AsyncAcceptanceCriteria criteria)
    {
        string cpu = criteria.CpuChangePercent?.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) ?? "n/a";
        string memory = criteria.MemoryChangePercent?.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) ?? "n/a";
        string raft = criteria.RaftEntryRatio?.ToString("F2", CultureInfo.InvariantCulture) ?? "n/a";
        return $"CPU {cpu}%, memory {memory}%, Raft x{raft}";
    }
=======
>>>>>>> origin/main
}
