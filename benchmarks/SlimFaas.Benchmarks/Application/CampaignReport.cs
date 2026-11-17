using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SlimFaasBenchmark;

// Offline adapters retain one row per repetition. Never infer per-operation CPU
// from the resource total of a mixed workload, or merge different platforms.
internal static class CampaignReport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> WriteAsync(string campaign, string output)
    {
        string root = Path.GetFullPath(campaign);
        string destination = Path.GetFullPath(output);
        if (!Directory.Exists(root) || Path.GetExtension(destination) != ".md")
            throw new ArgumentException("--campaign must exist and --output must end in .md.");
        string[] sources = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is "results.json" or "operations.json" or "feature-results.json" or "summary.json")
            .Order(StringComparer.Ordinal).ToArray();
        string[] outputs = [destination, Path.ChangeExtension(destination, ".json"), Path.ChangeExtension(destination, ".csv")];
        if (outputs.Any(path => sources.Contains(path, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Report output must not overwrite raw evidence.");
        var rows = new List<CampaignRow>();
        var evidence = new List<object>();
        var gaps = new List<string>();
        var repetitions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in sources)
        {
            byte[] raw = await File.ReadAllBytesAsync(path);
            string relative = Path.GetRelativePath(root, path);
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                JsonElement source = document.RootElement;
                string? parameters = FindParameters(root, Path.GetDirectoryName(path)!);
                string experiment = parameters is null ? relative : Path.GetRelativePath(root, Path.GetDirectoryName(parameters)!);
                int firstRow = rows.Count;
                if (parameters is not null)
                {
                    using JsonDocument metadata = JsonDocument.Parse(await File.ReadAllTextAsync(parameters));
                    double? count = Number(Property(metadata.RootElement, "arguments"), "repetitions");
                    if (count is > 0) repetitions[experiment] = (int)count.Value;
                }
                if (Number(Property(source, "Settings"), "Repetitions") is double expectedRepetitions)
                    repetitions[experiment] = (int)expectedRepetitions;
                evidence.Add(new { Path = relative, Sha256 = Convert.ToHexStringLower(SHA256.HashData(raw)),
                    Parameters = parameters is null ? null : Path.GetRelativePath(root, parameters),
                    Provenance = parameters is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(parameters)) });
                if (source.TryGetProperty("LatencyRuns", out JsonElement runs))
                {
                    foreach (JsonElement run in runs.EnumerateArray())
                        rows.Add(HttpRow(relative, run));
                    if (source.TryGetProperty("AsyncWorkloads", out JsonElement focused) && focused.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement run in focused.EnumerateArray()) rows.Add(HttpRow(relative, run, focused: true));
                }
                else if (source.TryGetProperty("report", out JsonElement report))
                {
                    JsonElement latency = report.GetProperty("Operations").EnumerateArray().First(row => Text(row, "Operation") == "All");
                    JsonElement resources = Property(source, "resources");
                    long failures = (long)(Number(report, "Failed") ?? 0) + (long)(Number(source, "validationFailures") ?? 0);
                    string scenario = Text(report, "Scenario") ?? "unknown";
                    string receiptsPath = Path.ChangeExtension(path, ".receipts.json");
                    bool? integrity = source.TryGetProperty("validationFailures", out _) ? failures == 0 : null;
                    if (File.Exists(receiptsPath))
                    {
                        using JsonDocument receipts = JsonDocument.Parse(await File.ReadAllTextAsync(receiptsPath));
                        integrity = integrity == true && Property(receipts.RootElement, "passed").ValueKind == JsonValueKind.True;
                    }
                    else if (scenario is "mixed" or "async" or "slimdata-mixed") integrity = null;
                    rows.Add(new CampaignRow(relative, scenario, "operation/s", Text(source, "resourceWindow") ?? "unknown",
                        (int?)Number(report, scenario.StartsWith("files", StringComparison.Ordinal) ? "FileBytes" : "PayloadBytes"), (int?)Number(report, "Concurrency"), (int)(Number(report, "Repetition") ?? 1),
                        Number(report, "Rate"), null, null, Number(latency, "P50Milliseconds"), Number(latency, "P95Milliseconds"), Number(latency, "P99Milliseconds"),
                        Number(resources, "CpuMillisecondsPerMessage"), MiB(resources), failures, integrity, scenario.StartsWith("files", StringComparison.Ordinal) ? Number(report, "Rate") * Number(report, "FileBytes") / 1048576d : null,
                        Verdict(failures, integrity, resources), Number(report, "ElapsedSeconds")));
                }
                else if (source.TryGetProperty("Rows", out JsonElement featureRows))
                {
                    foreach (JsonElement row in featureRows.EnumerateArray())
                    {
                        CampaignRow parsed = row.Deserialize<CampaignRow>() ?? throw new InvalidDataException("Empty feature row.");
                        rows.Add(parsed with { Source = relative });
                    }
                }
                else if (source.TryGetProperty("rate", out _))
                {
                    // Historical standalone stream/file reports have no verified
                    // all-node identity/window. Keep their observations descriptive.
                    rows.Add(new CampaignRow(relative, Text(source, "scenario") ?? "legacy", "operation/s", "legacy-unspecified",
                        (int?)Number(source, "payload_bytes"), (int?)Number(source, "concurrency"), 1,
                        Number(source, "rate"), null, null, Number(source, "p50_ms"), Number(source, "p95_ms"), Number(source, "p99_ms"),
                        Number(source, "cpu_ms_per_operation"), Number(source, "peak_rss_bytes") / 1048576d,
                        (long)(Number(source, "failed") ?? 0), null, null, "INCONCLUSIVE", Number(source, "elapsed_seconds")));
                }
                else gaps.Add(relative + ": unsupported legacy report shape");
                for (int index = firstRow; index < rows.Count; index++) rows[index] = rows[index] with { Experiment = experiment };
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
            { gaps.Add(relative + ": " + error.Message); }
        }
        foreach (string statusPath in Directory.GetFiles(root, "status.json", SearchOption.AllDirectories))
        {
            using JsonDocument status = JsonDocument.Parse(await File.ReadAllTextAsync(statusPath));
            if (Property(status.RootElement, "passed").ValueKind == JsonValueKind.False)
                gaps.Add(Path.GetRelativePath(root, statusPath) + ": execution failed; retain its logs even when no measurement was written");
        }
        var summaries = rows.GroupBy(row => new { row.Experiment, row.Scenario, row.Unit, row.Window, row.PayloadBytes, row.Concurrency })
            .Select(group => new { group.Key, Repetitions = group.Count(),
                CompleteRepetitions = !repetitions.TryGetValue(group.Key.Experiment!, out int count) || group.Select(row => row.Repetition).Order().SequenceEqual(Enumerable.Range(1, count)),
                Rate = Describe(group.Select(row => row.Rate)), CpuMillisecondsPerOperation = Describe(group.Select(row => row.CpuMillisecondsPerOperation)),
                PeakWorkingSetMiB = Describe(group.Select(row => row.PeakWorkingSetMiB)), Errors = group.Sum(row => row.Errors) }).ToArray();
        foreach (var group in summaries.Where(group => !group.CompleteRepetitions)) gaps.Add(group.Key + ": incomplete or duplicate repetitions");
        var comparisons = new List<object>();
        var comparisonVerdicts = new List<string>();
        foreach (string comparisonPath in Directory.GetFiles(root, "comparison.json", SearchOption.AllDirectories)
            .Concat(File.Exists(Path.Combine(root, "matrix.json")) ? [Path.Combine(root, "matrix.json")] : []))
        {
            using JsonDocument comparison = JsonDocument.Parse(await File.ReadAllTextAsync(comparisonPath));
            JsonElement value = comparison.RootElement;
            string comparisonVerdict = Text(value, "verdict") ?? (Property(value, "CandidateHasNoFailures").ValueKind == JsonValueKind.False ? "FAIL"
                : Text(value, "InconclusiveReason") is not null ? "INCONCLUSIVE"
                : Property(value, "Passed").ValueKind == JsonValueKind.True ? "PASS" : "FAIL");
            comparisonVerdicts.Add(comparisonVerdict);
            comparisons.Add(new { Path = Path.GetRelativePath(root, comparisonPath), Verdict = comparisonVerdict });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string verdict = rows.Any(row => row.Verdict == "FAIL") || comparisonVerdicts.Contains("FAIL", StringComparer.Ordinal) ? "FAIL" : comparisonVerdicts.Contains("INCONCLUSIVE", StringComparer.Ordinal) || gaps.Count != 0 || rows.Count == 0 || rows.Any(row => row.Verdict == "INCONCLUSIVE") ? "INCONCLUSIVE" : "OBSERVED";
        var export = new { SchemaVersion = 1, Campaign = root, Verdict = verdict, Rows = rows, Summaries = summaries, Evidence = evidence, Comparisons = comparisons, Gaps = gaps,
            Note = "OBSERVED means complete measurements and integrity, not performance non-regression. Use the ABBA comparison verdicts. Rows retain repetitions, windows and raw provenance." };
        await File.WriteAllTextAsync(outputs[1], JsonSerializer.Serialize(export, JsonOptions));
        var markdown = new StringBuilder("# Campaign measurements\n\n");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"Evidence status: **{verdict}**. Each row is one repetition; CPU and RAM cover runtime nodes only. `n/a` is missing evidence. Performance acceptance remains in the ABBA comparisons.\n");
        markdown.AppendLine("| Scenario | Unit | Bytes | C | Rep | Rate | Delivered/s | Completed/s | p50 ms | p95 ms | p99 ms | CPU ms/op | Peak MiB | Errors | Integrity | Window | Evidence | Raw |\n|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|---|");
        var csv = new StringBuilder("source,scenario,unit,payload_bytes,concurrency,repetition,rate,delivered_per_second,completed_per_second,p50_ms,p95_ms,p99_ms,cpu_ms_per_operation,peak_working_set_mib,errors,integrity,window,verdict,elapsed_seconds,mib_per_second\n");
        foreach (CampaignRow row in rows)
        {
            string integrity = row.Integrity is null ? "unknown" : row.Integrity.Value ? "PASS" : "FAIL";
            string[] cells = [row.Scenario, row.Unit, Format(row.PayloadBytes), Format(row.Concurrency), Format(row.Repetition), Format(row.Rate), Format(row.DeliveredPerSecond), Format(row.CompletedPerSecond), Format(row.P50Milliseconds), Format(row.P95Milliseconds), Format(row.P99Milliseconds), Format(row.CpuMillisecondsPerOperation), Format(row.PeakWorkingSetMiB), Format(row.Errors), integrity, row.Window, row.Verdict];
            string link = Path.GetRelativePath(Path.GetDirectoryName(destination)!, Path.Combine(root, row.Source)).Replace(Path.DirectorySeparatorChar, '/');
            markdown.AppendLine("| " + string.Join(" | ", cells.Select(cell => cell.Replace("|", "\\|", StringComparison.Ordinal))) + " | [raw](<" + link + ">) |");
            csv.AppendLine(string.Join(',', new[] { row.Source }.Concat(cells[..14]).Concat([integrity, row.Window, row.Verdict, Format(row.ElapsedSeconds), Format(row.MiBPerSecond)]).Select(EscapeCsv)));
        }
        foreach (string gap in gaps) markdown.AppendLine("\n- " + gap);
        await File.WriteAllTextAsync(destination, markdown.ToString());
        await File.WriteAllTextAsync(outputs[2], csv.ToString());
        return 0;
    }

    private static CampaignRow HttpRow(string source, JsonElement run, bool focused = false)
    {
        string mode = focused ? "async-" + Text(run, "LoadShape") : Text(run, "Mode") ?? "unknown";
        bool async = focused || mode == "async";
        JsonElement resources = Property(run, async ? focused ? "Resources" : "AsyncResources" : "Resources");
        JsonElement delivery = Property(run, focused ? "Delivery" : "AsyncDelivery");
        long failures = (long)(Number(run, "Failed") ?? 0) + (long)(Number(run, focused ? "Missing" : "AsyncMissing") ?? 0) + (long)(Number(run, focused ? "Duplicates" : "AsyncDuplicates") ?? 0);
        bool? integrity = async && delivery.ValueKind != JsonValueKind.Object ? null : failures == 0;
        double? completionWindow = Number(run, "CompletionWindowSeconds");
        return new CampaignRow(source, mode, async ? "accepted message/s" : "request/s", async ? "load+drain" : "load",
            (int?)Number(run, "PayloadBytes"), (int?)Number(run, "Concurrency"), (int)(Number(run, "Repetition") ?? 1),
            Number(run, "RatePerSecond"), completionWindow > 0 ? Number(delivery, "UniqueCount") / completionWindow : null,
            completionWindow > 0 ? Number(delivery, "Count") / completionWindow : null,
            Number(run, "P50Milliseconds"), Number(run, "P95Milliseconds"), Number(run, "P99Milliseconds"),
            Number(resources, async ? "CpuMillisecondsPerMessage" : "CpuMillisecondsPerRequest"), MiB(resources), failures, integrity, null,
            Verdict(failures, integrity, resources), Number(run, "ElapsedSeconds"));
    }

    private static object? Describe(IEnumerable<double?> values)
    {
        double?[] items = values.ToArray();
        if (items.Length == 0 || items.Any(value => value is null)) return null;
        double[] sorted = items.Select(value => value!.Value).Order().ToArray();
        return new { Minimum = sorted[0], Median = Statistics.Percentile(sorted, .5), Maximum = sorted[^1], Count = sorted.Length };
    }

    private static string? FindParameters(string root, string directory)
    {
        while (directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || directory == root)
        {
            string path = Path.Combine(directory, "parameters.json");
            if (File.Exists(path)) return path;
            directory = Path.GetDirectoryName(directory) ?? "";
        }
        return null;
    }

    private static string Verdict(long errors, bool? integrity, JsonElement resources) => errors > 0 || integrity == false ? "FAIL"
        : integrity is null || resources.ValueKind != JsonValueKind.Object || MiB(resources) is not > 0 || Number(resources, "CpuSeconds") is not >= 0 ? "INCONCLUSIVE" : "OBSERVED";
    private static double? MiB(JsonElement value) => Number(value, "PeakWorkingSetBytes") / 1048576d;
    private static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement property) ? property : default;
    private static string? Text(JsonElement value, string key) => Property(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static double? Number(JsonElement value, string key) => Property(value, key) is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double result) && double.IsFinite(result) ? result : null;
    private static string Format(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
    private static string EscapeCsv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

internal sealed record CampaignRow(string Source, string Scenario, string Unit, string Window,
    int? PayloadBytes, int? Concurrency, int Repetition, double? Rate, double? DeliveredPerSecond, double? CompletedPerSecond,
    double? P50Milliseconds, double? P95Milliseconds, double? P99Milliseconds, double? CpuMillisecondsPerOperation,
    double? PeakWorkingSetMiB, long Errors, bool? Integrity, double? MiBPerSecond, string Verdict, double? ElapsedSeconds, string? Experiment = null);
