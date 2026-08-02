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
    double BaselineHttpP50Milliseconds,
    double CandidateHttpP50Milliseconds,
    double? BaselineArrivalP50Milliseconds,
    double? CandidateArrivalP50Milliseconds,
    long CandidateFailed,
    string Verdict);

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
    ScalingComparison Scaling);

internal static class BenchmarkComparer
{
    private const double SmallPayloadMinimumReductionPercent = 20d;
    private const double LargePayloadMinimumReductionPercent = 40d;
    private const double MaximumGuardrailRegressionPercent = 10d;

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

        BenchmarkReport baseline = await ReadReportAsync(baselinePath);
        BenchmarkReport candidate = await ReadReportAsync(candidatePath);
        ValidateCompatibleSettings(baseline.Settings, candidate.Settings);

        BenchmarkComparisonReport comparison = BuildComparison(
            Path.GetFullPath(baselinePath),
            Path.GetFullPath(candidatePath),
            baseline,
            candidate);

        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "comparison.json"),
            JsonSerializer.Serialize(comparison, JsonOptions));
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "comparison.md"),
            BuildMarkdown(comparison));

        Console.WriteLine(comparison.Passed
            ? "PASS: the sync optimization met every comparison criterion."
            : "FAIL: one or more sync optimization criteria were not met.");
        Console.WriteLine($"Comparison artifacts: {outputDirectory}");
        return comparison.Passed ? 0 : 1;
    }

    internal static BenchmarkComparisonReport BuildComparison(
        string baselinePath,
        string candidatePath,
        BenchmarkReport baseline,
        BenchmarkReport candidate)
    {
        var failures = new List<string>();
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
                string verdict = guardrailsPassed && candidateLatency.Failed == 0
                    ? "PASS"
                    : "FAIL";

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

        if (!smallPassed)
            failures.Add(FormattableString.Invariant(
                $"Small-payload median p50 reduction was {smallReduction:F1}%, below {SmallPayloadMinimumReductionPercent:F1}%."));
        if (!largePassed)
            failures.Add(FormattableString.Invariant(
                $"Large-payload median p50 reduction was {largeReduction:F1}%, below {LargePayloadMinimumReductionPercent:F1}%."));
        foreach (SyncComparisonRow row in syncRows.Where(row => !row.GuardrailsPassed))
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
        bool noFailures = candidateLatencyFailures == 0 &&
                          candidate.Scaling.Failed == 0 &&
                          !candidate.Scaling.TimedOut;
        if (!noFailures)
        {
            failures.Add(
                $"Candidate recorded {candidateLatencyFailures} latency failures, " +
                $"{candidate.Scaling.Failed} scaling failures, timedOut={candidate.Scaling.TimedOut}.");
        }

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
            smallPassed && largePassed && guardrailsPassed && noFailures,
            failures,
            syncRows,
            asyncRows,
            scaling);
    }

    private static async Task<BenchmarkReport> ReadReportAsync(string path)
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

    private static string BuildMarkdown(BenchmarkComparisonReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SlimFaas sync optimization comparison");
        builder.AppendLine();
        builder.AppendLine($"Overall verdict: **{(report.Passed ? "PASS" : "FAIL")}**.");
        builder.AppendLine();
        builder.AppendLine("## Acceptance criteria");
        builder.AppendLine();
        builder.AppendLine("| criterion | observed | target | verdict |");
        builder.AppendLine("|---|---:|---:|:---:|");
        AppendCriterion(builder, "Small payload median added-p50 reduction",
            report.SmallPayloadMedianP50ReductionPercent, SmallPayloadMinimumReductionPercent,
            report.SmallPayloadTargetPassed);
        AppendCriterion(builder, "Large payload median added-p50 reduction",
            report.LargePayloadMedianP50ReductionPercent, LargePayloadMinimumReductionPercent,
            report.LargePayloadTargetPassed);
        builder.AppendLine($"| p95/p99 and throughput guardrails | — | max {MaximumGuardrailRegressionPercent:F0}% regression | {(report.SyncGuardrailsPassed ? "PASS" : "FAIL")} |");
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
        builder.AppendLine("## Asynchronous comparison (informational)");
        builder.AppendLine();
        builder.AppendLine("| payload | concurrency | baseline HTTP p50 | candidate HTTP p50 | baseline arrival p50 | candidate arrival p50 | candidate failures | verdict |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|:---:|");
        foreach (AsyncComparisonRow row in report.Async)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| {FormatBytes(row.PayloadBytes)} | {row.Concurrency} | {row.BaselineHttpP50Milliseconds:F3} ms | {row.CandidateHttpP50Milliseconds:F3} ms | {FormatNullable(row.BaselineArrivalP50Milliseconds)} | {FormatNullable(row.CandidateArrivalP50Milliseconds)} | {row.CandidateFailed} | {row.Verdict} |"));
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
}
