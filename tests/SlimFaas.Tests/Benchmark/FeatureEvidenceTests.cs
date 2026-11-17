using System.Text.Json;
using SlimFaas.Benchmarks;
using SlimFaas.Benchmarks.Support;
using SlimFaasBenchmark;

namespace SlimFaas.Tests.Benchmark;

public sealed class FeatureEvidenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(2000)]
    public void Queue_variants_preserve_order_and_identity_at_timeout_and_retry_boundaries(int count)
    {
        long now = DateTime.UtcNow.Ticks;
        var queue = BenchData.BuildMixedStateQueue(count, now);
        foreach (long instant in new[] { now - TimeSpan.TicksPerMinute, now, now + TimeSpan.TicksPerSecond, now + TimeSpan.TicksPerMinute })
        foreach (bool retry in new[] { false, true })
        {
            var expected = QueuePipelineBenchmarks.SelectZLinq(queue, instant, retry);
            var linq = QueuePipelineBenchmarks.SelectLinq(queue, instant, retry);
            var loop = QueuePipelineBenchmarks.SelectLoop(queue, instant, retry);
            Assert.Equal(expected.Length, linq.Length);
            Assert.Equal(expected.Length, loop.Length);
            for (int index = 0; index < expected.Length; index++)
            { Assert.Same(expected[index], linq[index]); Assert.Same(expected[index], loop[index]); }
        }
    }

    [Fact]
    public async Task Official_sdk_echo_streams_and_hashes_the_complete_response()
    {
        byte[] bytes = new byte[131079];
        new Random(42).NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        (long length, string hash) = await BenchmarkSdkTarget.CopyAndHashAsync(source, destination);
        Assert.Equal(bytes.Length, length);
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), hash);
    }

    [Fact]
    public void Receiver_reconciliation_detects_missing_fanout_duplicates_and_wrong_content()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string receipt = """{"id":"id","event":"started","function":"a","utc":1,"bytes":4,"sha256":"expected","status":200}""" + "\n";
            receipt += """{"id":"id","event":"completed","function":"a","utc":2,"bytes":4,"sha256":"expected","status":200}""" + "\n";
            File.WriteAllText(Path.Combine(directory, "receipts.jsonl"), receipt + "{\"partial\":");
            FeatureSample[] samples = [new("id", 0, 1, null, "queue", null)];
            FeatureReceipts result = FeatureRunner.ReadReceipts(directory, samples, "streams", ["a", "b"], 4, "expected");
            Assert.Equal(["id:b"], result.Missing);
            Assert.Empty(result.Duplicates);
            File.WriteAllText(Path.Combine(directory, "receipts.jsonl"), receipt + receipt.Replace("expected", "corrupted", StringComparison.Ordinal));
            result = FeatureRunner.ReadReceipts(directory, samples, "streams", ["a"], 4, "expected");
            Assert.Empty(result.Missing);
            Assert.Single(result.Duplicates);
            Assert.Equal(2, result.Corrupt.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Campaign_export_keeps_failed_integrity_when_resources_are_missing_and_preserves_raw_bytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "operations.json");
        string raw = """
            {"report":{"Scenario":"files","Concurrency":4,"PayloadBytes":1,"FileBytes":1048576,"Repetition":2,
            "Failed":1,"Rate":100,"ElapsedSeconds":10,"Operations":[{"Operation":"All","P50Milliseconds":1,"P95Milliseconds":2,"P99Milliseconds":3}]},"validationFailures":1}
            """;
        await File.WriteAllTextAsync(input, raw);
        try
        {
            string output = Path.Combine(directory, "export.md");
            Assert.Equal(0, await CampaignReport.WriteAsync(directory, output));
            using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(output, ".json")));
            Assert.Equal("FAIL", report.RootElement.GetProperty("Verdict").GetString());
            JsonElement row = report.RootElement.GetProperty("Rows")[0];
            Assert.Equal(2, row.GetProperty("Repetition").GetInt32());
            Assert.Equal(1048576, row.GetProperty("PayloadBytes").GetInt32());
            Assert.Equal(100, row.GetProperty("MiBPerSecond").GetDouble());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("CpuMillisecondsPerOperation").ValueKind);
            Assert.Equal(raw, await File.ReadAllTextAsync(input));
            Assert.Contains("operation/s", await File.ReadAllTextAsync(output));
            await Assert.ThrowsAsync<ArgumentException>(() => CampaignReport.WriteAsync(directory, Path.Combine(directory, "operations.md")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
