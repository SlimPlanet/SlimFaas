namespace SlimFaasBenchmark;

internal sealed class ClusterResourceMonitor : IAsyncDisposable
{
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _client;
    private readonly IReadOnlyList<Uri> _nodeUrls;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<ClusterResourceSnapshot> _samples;
    private readonly Task _monitoring;
    private Task<AsyncResourceUsage?>? _completion;

    private ClusterResourceMonitor(HttpClient client, IReadOnlyList<Uri> nodeUrls,
        ClusterResourceSnapshot initial)
    {
        _client = client;
        _nodeUrls = nodeUrls;
        _samples = [initial];
        _monitoring = MonitorAsync();
    }

    internal static async Task<ClusterResourceMonitor> StartAsync(HttpClient client,
        IReadOnlyList<Uri> nodeUrls)
    {
        using var deadline = new CancellationTokenSource(RefreshTimeout);
        ClusterResourceSnapshot previous = await ReadAsync(client, nodeUrls, deadline.Token).ConfigureAwait(false);
        ClusterResourceSnapshot initial = await WaitForRefreshAsync(client, nodeUrls, previous, deadline.Token)
            .ConfigureAwait(false);
        return new ClusterResourceMonitor(client, nodeUrls, initial);
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
                _samples.Add(await ReadAsync(_client, _nodeUrls, _cancellation.Token).ConfigureAwait(false));
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
        ClusterResourceSnapshot finalProcess = await ReadAsync(_client, _nodeUrls, deadline.Token)
            .ConfigureAwait(false);
        ClusterResourceSnapshot finalDiagnostics = await WaitForRefreshAsync(
            _client, _nodeUrls, finalProcess, deadline.Token).ConfigureAwait(false);
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
            raftEntries / messages,
            _samples.Max(sample => sample.WorkingSetBytes),
            cpuSeconds,
            raftEntries,
            initial,
            finalProcess,
            finalDiagnostics);
    }

    private static async Task<ClusterResourceSnapshot> WaitForRefreshAsync(HttpClient client,
        IReadOnlyList<Uri> nodeUrls, ClusterResourceSnapshot previous, CancellationToken token)
    {
        while (true)
        {
            ClusterResourceSnapshot current = await ReadAsync(client, nodeUrls, token).ConfigureAwait(false);
            if (current.Nodes.Zip(previous.Nodes).All(pair =>
                    pair.First.DiagnosticsCpuSeconds > pair.Second.DiagnosticsCpuSeconds))
                return current;
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    private static async Task<ClusterResourceSnapshot> ReadAsync(HttpClient client,
        IReadOnlyList<Uri> nodeUrls, CancellationToken token)
    {
        if (nodeUrls.Count == 0)
            throw new InvalidOperationException("Resource collection requires at least one node.");
        ClusterNodeResourceSnapshot[] nodes = await Task.WhenAll(nodeUrls.Select(async node =>
        {
            string metrics = await client.GetStringAsync(new Uri(node, "metrics"), token).ConfigureAwait(false);
            return new ClusterNodeResourceSnapshot(
                node.AbsoluteUri,
                DateTimeOffset.UtcNow,
                BenchmarkRunner.ReadRequiredMetric(metrics, "process_cpu_seconds_total"),
                BenchmarkRunner.ReadRequiredMetric(metrics, "slimdata_process_cpu_seconds_total"),
                BenchmarkRunner.ReadRequiredMetric(metrics, "slimdata_command_batch_raft_total"),
                BenchmarkRunner.ReadRequiredMetric(metrics, "process_working_set_bytes"));
        })).ConfigureAwait(false);
        if (nodes.Any(node => node.CpuSeconds < 0 || node.DiagnosticsCpuSeconds < 0 ||
                node.RaftEntries < 0 || node.WorkingSetBytes <= 0))
            throw new InvalidOperationException("Required resource counters are negative or working set is empty.");
        return new ClusterResourceSnapshot(nodes.Sum(node => node.CpuSeconds),
            nodes.Sum(node => node.RaftEntries), nodes.Sum(node => node.WorkingSetBytes), nodes);
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
    double CpuSeconds, double DiagnosticsCpuSeconds, double RaftEntries, double WorkingSetBytes);

internal sealed record ClusterResourceSnapshot(double CpuSeconds, double RaftEntries,
    double WorkingSetBytes, IReadOnlyList<ClusterNodeResourceSnapshot> Nodes);
