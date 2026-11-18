using System.Globalization;

namespace SlimFaasBenchmark;

internal sealed class ClusterResourceMonitor : IAsyncDisposable
{
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _client;
    private readonly IReadOnlyList<Uri> _nodeUrls;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<ClusterResourceSnapshot> _samples;
    private readonly Task _monitoring;
    private readonly bool _diagnostics;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private Task<AsyncResourceUsage?>? _completion;

    private ClusterResourceMonitor(HttpClient client, IReadOnlyList<Uri> nodeUrls,
        ClusterResourceSnapshot initial, bool diagnostics)
    {
        _client = client;
        _diagnostics = diagnostics;
        _nodeUrls = nodeUrls;
        _samples = [initial];
        _monitoring = MonitorAsync();
    }

    internal static async Task<ClusterResourceMonitor> StartAsync(HttpClient client,
        IReadOnlyList<Uri> nodeUrls, bool diagnostics = true)
    {
        using var deadline = new CancellationTokenSource(RefreshTimeout);
        ClusterResourceSnapshot previous = await ReadAsync(client, nodeUrls, deadline.Token, diagnostics).ConfigureAwait(false);
        ClusterResourceSnapshot initial = diagnostics
            ? await WaitForRefreshAsync(client, nodeUrls, previous, deadline.Token).ConfigureAwait(false)
            : previous;
        return new ClusterResourceMonitor(client, nodeUrls, initial, diagnostics);
    }

    internal Task<AsyncResourceUsage?> CompleteAsync(long messages)
        => _completion ??= CompleteCoreAsync(messages);

    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, _cancellation.Token).ConfigureAwait(false);
                ClusterResourceSnapshot current = await ReadAsync(_client, _nodeUrls, _cancellation.Token, _diagnostics)
                    .ConfigureAwait(false);
                ValidateContinuity(_samples[^1], current);
                _samples.Add(current);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task<AsyncResourceUsage?> CompleteCoreAsync(long messages)
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        await _monitoring.ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(RefreshTimeout);
        // CPU and RSS use live process collectors at the workload/drain boundary.
        // The five-second diagnostics gauges need a subsequent refresh on every
        // node before the final Raft count can be used, even for subsecond bursts.
        ClusterResourceSnapshot finalProcess = await ReadAsync(_client, _nodeUrls, deadline.Token, _diagnostics)
            .ConfigureAwait(false);
        ValidateContinuity(_samples[^1], finalProcess);
        DateTimeOffset endedAtUtc = DateTimeOffset.UtcNow;
        ClusterResourceSnapshot finalDiagnostics = _diagnostics
            ? await WaitForRefreshAsync(_client, _nodeUrls, finalProcess, deadline.Token).ConfigureAwait(false)
            : finalProcess;
        ClusterResourceSnapshot initial = _samples[0];
        _samples.Add(finalProcess);
        if (messages <= 0)
            return null;
        double cpuSeconds = finalProcess.CpuSeconds - initial.CpuSeconds;
        double raftEntries = finalDiagnostics.RaftEntries - initial.RaftEntries;
        if (cpuSeconds < 0 || raftEntries < 0)
            throw new InvalidOperationException("Resource counters reset during the workload; the measurement is incomplete.");
        return new AsyncResourceUsage(
            cpuSeconds * 1000d / messages,
            _diagnostics ? raftEntries / messages : null,
            _samples.Max(sample => sample.WorkingSetBytes),
            cpuSeconds,
            _diagnostics ? raftEntries : null,
            initial,
            finalProcess,
            _diagnostics ? finalDiagnostics : null,
            _startedAtUtc, endedAtUtc, _samples.ToArray());
    }

    private static async Task<ClusterResourceSnapshot> WaitForRefreshAsync(HttpClient client,
        IReadOnlyList<Uri> nodeUrls, ClusterResourceSnapshot previous, CancellationToken token)
    {
        ClusterResourceSnapshot last = previous;
        while (true)
        {
            ClusterResourceSnapshot current = await ReadAsync(client, nodeUrls, token).ConfigureAwait(false);
            ValidateContinuity(last, current);
            if (current.Nodes.Zip(previous.Nodes).All(pair =>
                    pair.First.DiagnosticsCpuSeconds > pair.Second.DiagnosticsCpuSeconds))
                return current;
            last = current;
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    private static void ValidateContinuity(ClusterResourceSnapshot previous, ClusterResourceSnapshot current)
    {
        if (current.Nodes.Count != previous.Nodes.Count || current.Nodes.Zip(previous.Nodes).Any(pair =>
                pair.First.Node != pair.Second.Node ||
                pair.First.ProcessStartTimeSeconds != pair.Second.ProcessStartTimeSeconds ||
                pair.First.CpuSeconds < pair.Second.CpuSeconds ||
                pair.First.DiagnosticsCpuSeconds < pair.Second.DiagnosticsCpuSeconds ||
                pair.First.RaftEntries < pair.Second.RaftEntries))
            throw new InvalidOperationException("A runtime node restarted or a resource counter reset; the measurement is incomplete.");
    }

    private static async Task<ClusterResourceSnapshot> ReadAsync(HttpClient client,
        IReadOnlyList<Uri> nodeUrls, CancellationToken token, bool diagnostics = true)
    {
        if (nodeUrls.Count == 0 || nodeUrls.Distinct().Count() != nodeUrls.Count)
            throw new InvalidOperationException("Resource collection requires at least one node.");
        ClusterNodeResourceSnapshot[] nodes = await Task.WhenAll(nodeUrls.Select(async node =>
        {
            string metrics = await client.GetStringAsync(new Uri(node, "metrics"), token).ConfigureAwait(false);
            return new ClusterNodeResourceSnapshot(
                node.AbsoluteUri,
                DateTimeOffset.UtcNow,
                ReadRequiredMetric(metrics, "process_cpu_seconds_total"),
                diagnostics ? ReadRequiredMetric(metrics, "slimdata_process_cpu_seconds_total") : 0,
                diagnostics ? ReadRequiredMetric(metrics, "slimdata_command_batch_raft_total") : 0,
                ReadRequiredMetric(metrics, "process_working_set_bytes"),
                ReadRequiredMetric(metrics, "process_start_time_seconds"));
        })).ConfigureAwait(false);
        if (nodes.Any(node => node.CpuSeconds < 0 || node.DiagnosticsCpuSeconds < 0 ||
                node.RaftEntries < 0 || node.WorkingSetBytes <= 0))
            throw new InvalidOperationException("Required resource counters are negative or working set is empty.");
        return new ClusterResourceSnapshot(nodes.Sum(node => node.CpuSeconds),
            nodes.Sum(node => node.RaftEntries), nodes.Sum(node => node.WorkingSetBytes), nodes);
    }

    internal static double ReadRequiredMetric(string metrics, string name)
    {
        foreach (ReadOnlySpan<char> line in metrics.AsSpan().EnumerateLines())
        {
            if (line.StartsWith(name + " ", StringComparison.Ordinal) &&
                double.TryParse(line[(name.Length + 1)..], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                return value;
        }
        throw new InvalidOperationException($"Required process metric '{name}' is missing or invalid.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            await _monitoring.ConfigureAwait(false);
            if (_completion is not null)
                await _completion.ConfigureAwait(false);
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}

internal sealed record ClusterNodeResourceSnapshot(string Node, DateTimeOffset CapturedAtUtc,
    double CpuSeconds, double DiagnosticsCpuSeconds, double RaftEntries, double WorkingSetBytes,
    double? ProcessStartTimeSeconds = null);

internal sealed record ClusterResourceSnapshot(double CpuSeconds, double RaftEntries,
    double WorkingSetBytes, IReadOnlyList<ClusterNodeResourceSnapshot> Nodes);

internal sealed record AsyncResourceUsage(
    double? CpuMillisecondsPerMessage,
    double? RaftEntriesPerMessage,
    double? PeakWorkingSetBytes,
    double? CpuSeconds,
    double? RaftEntries,
    ClusterResourceSnapshot? InitialSnapshot = null,
    ClusterResourceSnapshot? FinalProcessSnapshot = null,
    ClusterResourceSnapshot? FinalDiagnosticsSnapshot = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? FinishedAtUtc = null,
    IReadOnlyList<ClusterResourceSnapshot>? Samples = null);

