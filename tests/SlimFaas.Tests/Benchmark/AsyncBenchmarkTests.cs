using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Text;
using SlimFaasBenchmark;

namespace SlimFaas.Tests.Benchmark;

public sealed class AsyncBenchmarkTests
{
    [Theory]
    [InlineData("csv")]
    [InlineData("csv.gz")]
    public async Task Sample_writer_preserves_every_byte_and_completes_the_archive(string format)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "." + format);
        byte[] samples = Encoding.UTF8.GetBytes("mode,latency_ms,error\nsync,1.25,\nasync,2.5,\"quoted, ü\"\n");
        try
        {
            await using (Stream output = BenchmarkRunner.CreateSamplesStream(path, format))
                await output.WriteAsync(samples);
            await using FileStream input = File.OpenRead(path);
            await using Stream decoded = format == "csv.gz" ? new GZipStream(input, CompressionMode.Decompress) : input;
            using var bytes = new MemoryStream();
            await decoded.CopyToAsync(bytes);
            Assert.Equal(samples, bytes.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Comparer_rejects_different_sample_formats()
    {
        BenchmarkReport baseline = CompleteReport();
        BenchmarkReport candidate = baseline with { Settings = baseline.Settings with { SamplesFormat = "csv.gz" } };
        Assert.Throws<InvalidOperationException>(() => BenchmarkComparer.BuildComparison(
            "before", "after", baseline, candidate, criteria: "non-regression"));
    }

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
        Assert.True(double.IsNaN(BenchmarkRunner.ParseMetric(metrics, "missing")));
        Assert.True(double.IsNaN(BenchmarkRunner.ParseMetric("metric invalid\n", "metric")));
        Assert.Equal(0, BenchmarkRunner.ParseMetric("metric 0\n", "metric"));
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

    [Theory]
    [InlineData("metric 12.5", 12.5)]
    [InlineData("metric 0", 0)]
    [InlineData("metric_extra 999\nmetric 1e2", 100)]
    public void Required_process_metrics_accept_exact_finite_values(string metrics, double expected)
    {
        Assert.Equal(expected, BenchmarkRunner.ReadRequiredMetric(metrics, "metric"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("metric_extra 12.5")]
    [InlineData("metric{node=\"one\"} 12.5")]
    [InlineData("metric not-a-number")]
    [InlineData("metric NaN")]
    [InlineData("metric Infinity")]
    public void Required_process_metrics_fail_explicitly_when_missing_or_invalid(string metrics)
    {
        Assert.Throws<InvalidOperationException>(() => BenchmarkRunner.ReadRequiredMetric(metrics, "metric"));
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

    [Fact]
    public void Non_regression_accepts_unchanged_performance_without_optimization_gains()
    {
        BenchmarkReport report = CompleteReport();
        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison(
            "baseline", "candidate", report, report, criteria: "non-regression");

        Assert.True(comparison.Passed);
        Assert.Null(comparison.InconclusiveReason);
        Assert.Equal("non-regression", comparison.Criteria);
        Assert.False(BenchmarkComparer.BuildComparison("baseline", "candidate", report, report, "async").Passed);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("memory")]
    [InlineData("raft")]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("delivery")]
    [InlineData("burst")]
    public void Non_regression_enforces_both_profiles_and_resource_guards(string regression)
    {
        BenchmarkReport baseline = CompleteReport();
        BenchmarkReport candidate = regression switch
        {
            "sync" => baseline with { SyncOverhead = [baseline.SyncOverhead[0] with { AddedP99Milliseconds = 2 }] },
            "burst" => baseline with { AsyncWorkloads = baseline.AsyncWorkloads!.Select(row =>
                row.LoadShape == "burst" ? row with { RatePerSecond = 5 } : row).ToArray() },
            _ => baseline with { Latency = baseline.Latency.Select(row => row.Mode != "async" ? row : regression switch
            {
                "cpu" => row with { AsyncCpuMillisecondsPerMessage = 13 },
                "memory" => row with { AsyncPeakWorkingSetBytes = 120 },
                "raft" => row with { AsyncRaftEntriesPerMessage = 5 },
                "async" => row with { P99Milliseconds = 300 },
                _ => row with { AsyncMissing = 1 }
            }).ToArray() }
        };

        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison(
            "baseline", "candidate", baseline, candidate, "async", "non-regression");

        Assert.False(comparison.Passed);
        Assert.Null(comparison.InconclusiveReason);
        Assert.NotEmpty(comparison.Failures);
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("case")]
    [InlineData("repetition")]
    [InlineData("workload")]
    [InlineData("scaling")]
    [InlineData("baseline-failure")]
    [InlineData("matrix")]
    public void Non_regression_cannot_pass_incomplete_or_failed_reference_evidence(string missing)
    {
        BenchmarkReport report = CompleteReport();
        BenchmarkReport incomplete = missing switch
        {
            "resource" => report with { Latency = report.Latency.Select(row =>
                row.Mode == "async" ? row with { AsyncCpuMillisecondsPerMessage = null } : row).ToArray() },
            "case" => report with { Latency = report.Latency.Where(row => row.Mode != "sync").ToArray() },
            "repetition" => report with { LatencyRuns = report.LatencyRuns.Skip(1).ToArray() },
            "workload" => report with { AsyncWorkloads = [] },
            "scaling" => report with { Scaling = report.Scaling with { QueueDrainedMilliseconds = null } },
            "matrix" => report with { Settings = report.Settings with { PayloadBytes = [] } },
            _ => report with { Latency = report.Latency.Select(row => row with { Failed = 1 }).ToArray() }
        };

        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison(
            "baseline", "candidate", incomplete, missing == "matrix" ? incomplete : report, criteria: "non-regression");

        Assert.False(comparison.Passed);
        Assert.NotNull(comparison.InconclusiveReason);
        Assert.True(comparison.CandidateHasNoFailures);
    }

    [Theory]
    [InlineData("paced", "cpu")]
    [InlineData("burst", "cpu")]
    [InlineData("burst", "memory")]
    [InlineData("burst", "raft")]
    public void Non_regression_checks_focused_workload_resource_budgets(string shape, string resource)
    {
        BenchmarkReport baseline = CompleteReport();
        BenchmarkReport candidate = baseline with
        {
            AsyncWorkloads = baseline.AsyncWorkloads!.Select(row => row.LoadShape != shape ? row : row with
            {
                Resources = resource switch
                {
                    "cpu" => row.Resources! with { CpuMillisecondsPerMessage = 13 },
                    "memory" => row.Resources! with { PeakWorkingSetBytes = 120 },
                    _ => row.Resources! with { RaftEntriesPerMessage = 5 }
                }
            }).ToArray()
        };

        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison(
            "baseline", "candidate", baseline, candidate, criteria: "non-regression");

        Assert.False(comparison.Passed);
        Assert.Null(comparison.InconclusiveReason);
        Assert.Contains(comparison.Failures, failure => failure.Contains(shape, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Offline_report_keeps_throughput_resource_units_and_incomplete_evidence(bool missingResource)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            BenchmarkReport report = CompleteReport();
            report = report with
            {
                LatencyRuns = report.LatencyRuns.Select(row => row with
                {
                    RatePerSecond = 10 * row.Repetition,
                    Failed = 2,
                    AsyncMissing = 1,
                    AsyncDuplicates = 1,
                    AsyncResources = missingResource && row.Repetition == 1 ? null :
                        row.AsyncResources! with { PeakWorkingSetBytes = 3 * 1024 * 1024 },
                    Resources = new ProcessResourceUsage(1, 999, 9 * 1024 * 1024)
                }).ToArray()
            };
            string input = Path.Combine(directory, "results.json");
            string output = Path.Combine(directory, "report.md");
            string json = JsonSerializer.Serialize(report);
            await File.WriteAllTextAsync(input, json);

            Assert.Equal(0, await SlimFaas.Benchmarks.Program.Main(["report", "--input", input, "--output", output]));
            string markdown = await File.ReadAllTextAsync(output);
            string asyncRow = Assert.Single(markdown.Split('\n'), line => line.StartsWith("| async |", StringComparison.Ordinal));
            Assert.Contains("20.0 accepted msg/s", asyncRow);
            Assert.Contains("| 6 | 3/3 |", asyncRow);
            Assert.Contains(missingResource ? "unavailable | unavailable" : "10.000 ms | 3.000 MiB", asyncRow);
            Assert.Contains("20.0 req/s", markdown);
            Assert.Equal(json, await File.ReadAllTextAsync(input));
            Assert.Equal(1, await SlimFaas.Benchmarks.Program.Main(["report", "--input", input, "--output", input]));
            Assert.Equal(json, await File.ReadAllTextAsync(input));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void One_missing_repetition_cannot_be_hidden_by_the_aggregate_median()
    {
        BenchmarkReport report = CompleteReport();
        BenchmarkReport candidate = report with { LatencyRuns = report.LatencyRuns.Select(row =>
            row.Mode == "async" && row.Repetition == 1 ? row with { AsyncResources = null } : row).ToArray() };
        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison("before", "after", report, candidate, criteria: "non-regression");
        Assert.NotNull(comparison.InconclusiveReason);
        Assert.False(comparison.Passed);
    }

    [Fact]
    public void Candidate_integrity_failure_is_retained_when_reference_metrics_are_incomplete()
    {
        BenchmarkReport report = CompleteReport();
        BenchmarkReport candidate = report with { LatencyRuns = report.LatencyRuns.Select(row =>
            row.Mode == "async" && row.Repetition == 1 ? row with { AsyncMissing = 1 } : row).ToArray() };
        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison("before", "after",
            report with { LatencyRuns = [] }, candidate, criteria: "non-regression");
        Assert.NotNull(comparison.InconclusiveReason);
        Assert.False(comparison.CandidateHasNoFailures);
        Assert.False(comparison.Passed);
    }

    [Fact]
    public void Per_repetition_failure_cannot_be_hidden_by_a_successful_aggregate()
    {
        BenchmarkReport baseline = CompleteReport();
        BenchmarkReport candidate = baseline with { LatencyRuns = baseline.LatencyRuns.Select(row =>
            row.Mode == "async" && row.Repetition == 1 ? row with { AsyncMissing = 1 } : row).ToArray() };
        BenchmarkComparisonReport comparison = BenchmarkComparer.BuildComparison("before", "after", baseline, candidate, criteria: "non-regression");
        Assert.False(comparison.Passed);
        Assert.False(comparison.CandidateHasNoFailures);
    }

    private static BenchmarkReport CompleteReport()
    {
        LatencyAggregateResult async = Row(150, 100, 200, 220, 300, 200, 400, 450, 100, 10, 2, 100);
        LatencyAggregateResult[] rows = [async with { Mode = "direct" }, async with { Mode = "sync" }, async];
        var resources = new AsyncResourceUsage(10, 2, 100, 1, 1);
        var delivery = new TargetObservationSummary("run", 100, 200, 400, 450, 200, 400, 450, 100);
        return Report(async) with
        {
            Latency = rows,
            LatencyRuns = rows.SelectMany(row => Enumerable.Range(1, 3).Select(repetition =>
                new LatencyRunResult(row.Mode, row.PayloadBytes, row.Concurrency, repetition,
                    100, 0, 10, 10, 100, 200, 220, 220, delivery, AsyncResources: resources))).ToArray(),
            SyncOverhead = [new SyncOverheadResult(2 * 1024 * 1024, 1,
                1, 2, 1, 100, 1, 2, 1, 100, 1, 2, 1, 100, 100, 90, 0.9)],
            AsyncWorkloads = new[] { "paced", "burst" }.SelectMany(shape => Enumerable.Range(1, 3).Select(repetition =>
                new AsyncWorkloadResult(shape, 4096, shape == "paced" ? 1 : 64, repetition,
                    100, 100, 0, 0, 0, 10, 10, 150, 100, 200, 220, delivery, resources))).ToArray()
        };
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
