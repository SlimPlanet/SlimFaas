namespace SlimFaasBenchmark;

internal static partial class BenchmarkComparer
{
    private static BenchmarkComparisonReport BuildNonRegressionComparison(
        string baselinePath, string candidatePath, BenchmarkReport baseline, BenchmarkReport candidate)
    {
        ValidateCompatibleSettings(baseline.Settings, candidate.Settings);
        string[] incomplete = [.. FindIncompleteEvidence(baseline, "Baseline"),
            .. FindIncompleteEvidence(candidate, "Candidate")];
        if (incomplete.Length > 0)
        {
            return new BenchmarkComparisonReport(
                DateTimeOffset.UtcNow, baselinePath, candidatePath, null, null, false, false,
                false, candidate.Latency.All(row => row.Failed == 0 && row.AsyncMissing == 0 && row.AsyncDuplicates == 0) &&
                       (candidate.AsyncWorkloads?.All(row => row.Failed == 0 && row.Missing == 0 && row.Duplicates == 0) ?? true) &&
                       candidate.Scaling.Failed == 0 && !candidate.Scaling.TimedOut,
                false, incomplete, [], [],
                new ScalingComparison(null, null, null, null, null, null,
                    candidate.Scaling.Failed, candidate.Scaling.TimedOut, "INCONCLUSIVE"),
                Profile: "sync+async", Criteria: "non-regression", InconclusiveReason: string.Join(" ", incomplete));
        }

        BenchmarkComparisonReport sync = BuildComparison(baselinePath, candidatePath, baseline, candidate);
        BenchmarkComparisonReport async = BuildComparison(baselinePath, candidatePath, baseline, candidate, "async");
        AsyncAcceptanceCriteria resources = async.AsyncCriteria!;
        var failures = new List<string>();
        foreach (SyncComparisonRow row in sync.Sync.Where(row => !row.GuardrailsPassed))
            failures.Add($"Sync p95/p99/throughput guardrail failed for {row.PayloadBytes} bytes at concurrency {row.Concurrency}.");
        foreach (AsyncComparisonRow row in async.Async.Where(row => row.Verdict != "PASS"))
            failures.Add($"Async p99/throughput/delivery guardrail failed for {row.PayloadBytes} bytes at concurrency {row.Concurrency}.");
        foreach (AsyncWorkloadComparisonRow row in async.AsyncWorkloads!.Where(row => row.Verdict != "PASS"))
            failures.Add($"Async {row.LoadShape} workload guardrail failed at concurrency {row.Concurrency}.");
        if (!resources.ResourceGuardrailsPassed)
            failures.Add($"Async resource guardrail failed: {FormatResources(resources)}.");
        if (!sync.CandidateHasNoFailures)
            failures.AddRange(sync.Failures.Where(failure => failure.StartsWith("Candidate recorded", StringComparison.Ordinal)));

        return sync with
        {
            Criteria = "non-regression",
            Profile = "sync+async",
            Passed = failures.Count == 0,
            Failures = failures,
            Async = async.Async,
            AsyncCriteria = resources,
            AsyncWorkloads = async.AsyncWorkloads
        };
    }

    private static IEnumerable<string> FindIncompleteEvidence(BenchmarkReport report, string variant)
    {
        if (report.Settings.PayloadBytes.Count == 0 || report.Settings.Concurrency.Count == 0 || report.Settings.Repetitions <= 0)
        {
            yield return $"{variant} has no complete measurement matrix.";
            yield break;
        }
        if (variant == "Baseline" && (report.Latency.Any(row => row.Failed > 0 || row.AsyncMissing > 0 || row.AsyncDuplicates > 0) ||
            report.AsyncWorkloads?.Any(row => row.Failed > 0 || row.Missing > 0 || row.Duplicates > 0) == true ||
            report.Scaling.Failed > 0 || report.Scaling.TimedOut))
            yield return "Baseline contains workload failures and cannot certify non-regression.";

        foreach (int bytes in report.Settings.PayloadBytes)
        foreach (int concurrency in report.Settings.Concurrency)
        {
            foreach (string mode in new[] { "direct", "sync", "async" })
            {
                LatencyAggregateResult[] rows = report.Latency.Where(row => row.Mode == mode &&
                    row.PayloadBytes == bytes && row.Concurrency == concurrency).ToArray();
                LatencyRunResult[] runs = report.LatencyRuns.Where(row => row.Mode == mode &&
                    row.PayloadBytes == bytes && row.Concurrency == concurrency).ToArray();
                if (rows.Length != 1 || rows[0].Repetitions != report.Settings.Repetitions || rows[0].Completed <= 0 ||
                    runs.Length != report.Settings.Repetitions ||
                    !runs.Select(row => row.Repetition).Order().SequenceEqual(Enumerable.Range(1, report.Settings.Repetitions)))
                {
                    yield return $"{variant} lacks complete {mode} repetitions for {bytes} bytes/concurrency {concurrency}.";
                    continue;
                }
                if (mode == "async" && (rows[0].AsyncArrivalP99Milliseconds is null || rows[0].AsyncObserved is null ||
                    !PositiveFinite(rows[0].AsyncCpuMillisecondsPerMessage) ||
                    !PositiveFinite(rows[0].AsyncRaftEntriesPerMessage) ||
                    !PositiveFinite(rows[0].AsyncPeakWorkingSetBytes)))
                    yield return $"{variant} lacks async arrival/resource evidence for {bytes} bytes/concurrency {concurrency}.";
            }
            if (report.SyncOverhead.Count(row => row.PayloadBytes == bytes && row.Concurrency == concurrency) != 1)
                yield return $"{variant} lacks normalized sync evidence for {bytes} bytes/concurrency {concurrency}.";
        }

        if (report.Settings.Profile == "async-queue")
        {
            foreach (string shape in new[] { "paced", "burst" })
            {
                AsyncWorkloadResult[] rows = report.AsyncWorkloads?.Where(row => row.LoadShape == shape).ToArray() ?? [];
                if (rows.Length != report.Settings.Repetitions || rows.Any(row => row.Requested <= 0) ||
                    !rows.Select(row => row.Repetition).Order().SequenceEqual(Enumerable.Range(1, report.Settings.Repetitions)))
                    yield return $"{variant} lacks complete {shape} workload repetitions.";
                if (rows.Any(row => row.Resources is null || !PositiveFinite(row.Resources.CpuMillisecondsPerMessage) ||
                    !PositiveFinite(row.Resources.RaftEntriesPerMessage) || !PositiveFinite(row.Resources.PeakWorkingSetBytes)))
                    yield return $"{variant} lacks {shape} resource evidence.";
            }
        }
        if (report.Scaling.ReadyOneMilliseconds is null || report.Scaling.ReadyTargetMilliseconds is null ||
            report.Scaling.QueueDrainedMilliseconds is null || report.Scaling.Accepted != report.Scaling.Messages)
            yield return $"{variant} lacks complete scaling evidence.";
    }

    private static bool PositiveFinite(double? value) => value is > 0 && double.IsFinite(value.Value);
}
