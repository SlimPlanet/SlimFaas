using System.Globalization;
using SlimFaas.Kubernetes;

namespace SlimFaas.Local;

public sealed class LocalNodeManager : IAsyncDisposable
{
    private readonly LoadedLocalManifest _loaded;
    private readonly LocalStateStore _state;
    private readonly Uri _supervisorUri;
    private readonly string _token;
    private readonly HttpClient _healthClient = new();
    private readonly List<NodeRuntime> _nodes;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _monitorTask;

    public LocalNodeManager(
        LoadedLocalManifest loaded,
        LocalStateStore state,
        Uri supervisorUri,
        string token)
    {
        _loaded = loaded;
        _state = state;
        _supervisorUri = supervisorUri;
        _token = token;
        _nodes = Enumerable.Range(0, loaded.Manifest.Cluster.Nodes)
            .Select(index => new NodeRuntime(index))
            .ToList();
    }

    public IReadOnlyList<int> ReadyHttpPorts
    {
        get
        {
            lock (_nodes)
                return _nodes.Where(node => node.Ready).Select(HttpPort).ToList();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (NodeRuntime node in _nodes)
            StartNode(node);
        _monitorTask = MonitorLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public IList<PodInformation> Snapshot()
    {
        lock (_nodes)
        {
            return _nodes.Select(node => new PodInformation(
                Name: $"slimfaas-{node.Index}",
                Started: node.Process is { HasExited: false },
                Ready: node.Ready,
                Ip: "127.0.0.1",
                DeploymentName: "slimfaas",
                Ports: [RaftPort(node), HttpPort(node)],
                ResourceVersion: node.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
                ServiceName: "slimfaas")
            {
                Annotations = MetricsAnnotations(HttpPort(node)),
                AppFailureReason = node.FailureReason,
                AppFailureMessage = node.FailureMessage
            }).ToList();
        }
    }

    internal static IDictionary<string, string> MetricsAnnotations(int httpPort)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prometheus.io/scrape"] = "true",
            ["prometheus.io/path"] = "/metrics",
            ["prometheus.io/port"] = httpPort.ToString(CultureInfo.InvariantCulture)
        };

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                foreach (NodeRuntime node in _nodes)
                {
                    bool restart = false;
                    ManagedLocalProcess? exitedProcess = null;
                    lock (_nodes)
                    {
                        if (node.Process is { HasExited: true } process)
                        {
                            node.FailureReason = "ProcessExited";
                            node.FailureMessage = $"SlimFaas node exited with code {process.ExitCode}.";
                            exitedProcess = process;
                            node.Process = null;
                            node.Ready = false;
                            int failures = ++node.ConsecutiveFailures;
                            node.NextStart = DateTimeOffset.UtcNow.AddSeconds(
                                Math.Min(30, 1 << Math.Min(failures - 1, 5)));
                        }

                        restart = node.Process is null && DateTimeOffset.UtcNow >= node.NextStart;
                    }

                    if (exitedProcess is not null)
                        await exitedProcess.DisposeAsync();
                    if (restart)
                        StartNode(node);
                    await ProbeAsync(node, cancellationToken);
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StartNode(NodeRuntime node)
    {
        lock (_nodes)
        {
            if (node.Process is not null)
                return;

            IReadOnlyList<string> command = SelfCommand();
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOSTNAME"] = $"slimfaas-{node.Index}",
                ["SlimFaas__Orchestrator"] = "Process",
                ["SlimFaas__Namespace"] = _loaded.Manifest.Name,
                ["SlimFaas__Process__SupervisorUrl"] = _supervisorUri.ToString().TrimEnd('/'),
                ["SlimFaas__Process__Token"] = _token,
                ["SlimFaas__BaseSlimDataUrl"] = "http://{pod_ip}:{pod_port_0}",
                ["SlimFaas__BaseFunctionUrl"] = "http://{pod_ip}:{pod_port}",
                ["SlimFaas__BaseFunctionPodUrl"] = "http://{pod_ip}:{pod_port}",
                ["SlimFaas__WebSocketPort"] = "0",
                ["SlimData__Directory"] = _state.DataDirectory,
                ["SlimData__AllowColdStart"] = "true",
                ["Data__DefaultVisibility"] = "Public",
                ["Logging__LogLevel__Default"] = "Warning",
                ["Logging__LogLevel__Microsoft.AspNetCore"] = "Error"
            };

            try
            {
                node.Process = ManagedLocalProcess.Start(
                    command,
                    AppContext.BaseDirectory,
                    environment,
                    $"node/slimfaas-{node.Index}",
                    Path.Combine(_state.LogsDirectory, $"slimfaas-{node.Index}.log"));
                node.StartedAt = DateTimeOffset.UtcNow;
                node.FailureReason = null;
                node.FailureMessage = null;
            }
            catch (Exception exception)
            {
                node.FailureReason = "ProcessStartFailed";
                node.FailureMessage = exception.Message;
                int failures = ++node.ConsecutiveFailures;
                node.NextStart = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Min(30, 1 << Math.Min(failures - 1, 5)));
            }
        }
    }

    private async Task ProbeAsync(NodeRuntime node, CancellationToken cancellationToken)
    {
        lock (_nodes)
        {
            if (node.Process is null)
            {
                node.Ready = false;
                return;
            }
        }

        bool ready;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            using HttpResponseMessage response = await _healthClient.GetAsync(
                $"http://127.0.0.1:{HttpPort(node)}/ready",
                timeout.Token);
            ready = response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException)
        {
            ready = false;
        }

        lock (_nodes)
        {
            node.Ready = ready;
            if (ready)
                node.ConsecutiveFailures = 0;
        }
    }

    private IReadOnlyList<string> SelfCommand()
    {
        string processPath = Environment.ProcessPath
                             ?? throw new InvalidOperationException("Unable to locate the SlimFaas executable.");
        string processName = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assemblyPath = Environment.GetCommandLineArgs()[0];
            if (!Path.IsPathFullyQualified(assemblyPath))
                assemblyPath = Path.GetFullPath(assemblyPath);
            return [processPath, assemblyPath];
        }

        return [processPath];
    }

    private int HttpPort(NodeRuntime node) => _loaded.Manifest.Cluster.NodeHttpPortBase + node.Index;
    private int RaftPort(NodeRuntime node) => _loaded.Manifest.Cluster.RaftPortBase + node.Index;

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_monitorTask is not null)
            await _monitorTask;

        foreach (NodeRuntime node in _nodes)
        {
            ManagedLocalProcess? process;
            lock (_nodes)
            {
                process = node.Process;
                node.Process = null;
                node.Ready = false;
            }

            if (process is not null)
            {
                await process.StopAsync(null, TimeSpan.FromSeconds(5));
                await process.DisposeAsync();
            }
        }

        _healthClient.Dispose();
        _stopping.Dispose();
    }

    private sealed class NodeRuntime(int index)
    {
        public int Index { get; } = index;
        public ManagedLocalProcess? Process { get; set; }
        public bool Ready { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset NextStart { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? FailureReason { get; set; }
        public string? FailureMessage { get; set; }
    }
}
