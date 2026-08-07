using System.Text.Json;
using System.Text.Json.Nodes;
using SlimFaasBenchmark;

namespace SlimFaas.Tests.Benchmark;

public sealed class AsyncBenchmarkTests
{
    [Fact]
    public void Statistics_compute_interpolated_percentiles_and_mean_inputs()
    {
        double[] values = [1, 2, 3, 4, 5];

        Assert.Equal(3, Statistics.Percentile(values, 0.50));
        Assert.Equal(4.8, Statistics.Percentile(values, 0.95), precision: 10);
        Assert.Equal(3, Statistics.Median(values));
    }

    [Fact]
    public void Target_observations_count_unique_missing_and_duplicate_messages()
    {
        var observations = new ObservationAccumulator();
        observations.Record("one", 10, 12);
        observations.Record("one", 11, 13);
        observations.Record("two", 20, 22);

        TargetObservationSummary summary = observations.Snapshot("run");

        Assert.Equal(3, summary.Count);
        Assert.Equal(2, summary.UniqueCount);
        Assert.Equal(1, summary.DuplicateCount);
        Assert.Equal(15, summary.ArrivalMeanMilliseconds);
        Assert.Equal(17, summary.CompletionMeanMilliseconds);
    }

    [Fact]
    public void Prometheus_parser_reads_exact_unlabelled_metric()
    {
        const string metrics = """
                               # TYPE metric counter
                               metric_extra 100
                               metric 12.5
                               """;

        Assert.Equal(12.5, BenchmarkRunner.ParseMetric(metrics, "metric"));
        Assert.Equal(0, BenchmarkRunner.ParseMetric(metrics, "missing"));
    }

    [Fact]
    public void Async_comparison_passes_when_latency_and_resource_targets_are_met()
    {
        BenchmarkReport baseline = Report(Row(
            mean: 150,
            p50: 100,
            p95: 200,
            p99: 220,
            arrivalMean: 300,
            arrivalP50: 200,
            arrivalP95: 400,
            arrivalP99: 450,
            rate: 100,
            cpu: 10,
            raft: 2,
            memory: 100));
        BenchmarkReport candidate = Report(Row(
            mean: 70,
            p50: 50,
            p95: 80,
            p99: 200,
            arrivalMean: 140,
            arrivalP50: 100,
            arrivalP95: 200,
            arrivalP99: 400,
            rate: 100,
            cpu: 11,
            raft: 3,
            memory: 105));

        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison(
            "baseline.json", "candidate.json", baseline, candidate, "async");

        Assert.True(comparison.Passed);
        Assert.NotNull(comparison.AsyncCriteria);
        Assert.True(comparison.AsyncCriteria.HttpP95TargetPassed);
        Assert.True(comparison.AsyncCriteria.ArrivalP95TargetPassed);
        Assert.True(comparison.AsyncCriteria.LargePayloadTargetPassed);
        Assert.True(comparison.AsyncCriteria.ResourceGuardrailsPassed);
    }

    [Fact]
    public void Async_comparison_fails_on_missing_or_duplicate_delivery()
    {
        LatencyAggregateResult baselineRow = Row(
            150, 100, 200, 220, 300, 200, 400, 450, 100, 10, 2, 100);
        LatencyAggregateResult candidateRow = Row(
            70, 50, 80, 200, 140, 100, 200, 400, 100, 11, 3, 105) with
        {
            AsyncMissing = 1,
            AsyncDuplicates = 1
        };

        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison(
            "baseline.json", "candidate.json", Report(baselineRow), Report(candidateRow), "async");

        Assert.False(comparison.Passed);
        Assert.False(comparison.CandidateHasNoFailures);
    }

    [Fact]
    public void Async_profile_reports_but_does_not_gate_on_sync_microbenchmark_noise()
    {
        BenchmarkReport baseline = Report(Row(
            150, 100, 200, 220, 300, 200, 400, 450, 100, 10, 2, 100));
        BenchmarkReport candidate = Report(Row(
            70, 50, 80, 200, 140, 100, 200, 400, 100, 11, 3, 105));
        var baselineSync = new SyncOverheadResult(
            2 * 1024 * 1024, 1,
            1, 2, 1, 100,
            1, 2, 1, 100,
            1, 2, 1, 100,
            100, 90, 0.9);
        baseline = baseline with
        {
            SyncOverhead = [baselineSync],
            Latency = [.. baseline.Latency, baseline.Latency[0] with { Mode = "sync" }]
        };
        candidate = candidate with
        {
            SyncOverhead = [baselineSync with { AddedP99Milliseconds = 2 }],
            Latency = [.. candidate.Latency, candidate.Latency[0] with { Mode = "sync" }]
        };

        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison(
            "baseline.json", "candidate.json", baseline, candidate, "async");

        Assert.True(comparison.Passed);
        Assert.False(comparison.SyncGuardrailsPassed);
        Assert.All(comparison.Sync, row => Assert.Equal("INFO", row.Verdict));
        Assert.DoesNotContain(comparison.Failures, failure => failure.StartsWith("Guardrail failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reader_accepts_result_json_without_new_optional_async_fields()
    {
        BenchmarkReport report = Report(Row(
            150, 100, 200, 220, 300, 200, 400, 450, 100, 10, 2, 100));
        JsonNode root = JsonSerializer.SerializeToNode(report)!;
        root.AsObject().Remove("AsyncWorkloads");
        root["Settings"]!.AsObject().Remove("Profile");
        foreach (JsonNode? latency in root["Latency"]!.AsArray())
        {
            latency!.AsObject().Remove("AsyncMissing");
            latency.AsObject().Remove("AsyncDuplicates");
            latency.AsObject().Remove("MeanMilliseconds");
        }
        string path = Path.Combine(Path.GetTempPath(), $"slimfaas-benchmark-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, root.ToJsonString());

            BenchmarkReport loaded = await BenchmarkComparer.ReadReportAsync(path);

            Assert.Equal("standard", loaded.Settings.Profile);
            Assert.Null(loaded.AsyncWorkloads);
            Assert.Equal(0, Assert.Single(loaded.Latency).AsyncMissing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static BenchmarkReport Report(LatencyAggregateResult row)
    {
        var settings = new BenchmarkSettings(
            "http://slimfaas",
            "http://target",
            ["http://node"],
            "function",
            "scale",
            10,
            2,
            3,
            [2 * 1024 * 1024],
            [1],
            200,
            32,
            4,
            "async-queue",
            100,
            100,
            1_000,
            64);
        var scaling = new ScaleResult(200, 32, 4, 200, 0, 1, 2, 3, 4, 5, 6, 10, 0, false);
        return new BenchmarkReport(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "test",
            ".NET",
            1,
            settings,
            [],
            [row],
            [],
            scaling,
            []);
    }

    private static LatencyAggregateResult Row(
        double mean,
        double p50,
        double p95,
        double p99,
        double arrivalMean,
        double arrivalP50,
        double arrivalP95,
        double arrivalP99,
        double rate,
        double cpu,
        double raft,
        double memory) =>
        new(
            "async",
            2 * 1024 * 1024,
            1,
            3,
            300,
            0,
            rate,
            p50,
            p95,
            p99,
            p99,
            arrivalP50,
            arrivalP95,
            arrivalP99,
            arrivalP50,
            arrivalP95,
            arrivalP99,
            300,
            mean,
            arrivalMean,
            arrivalMean,
            0,
            0,
            cpu,
            raft,
            memory);
}
