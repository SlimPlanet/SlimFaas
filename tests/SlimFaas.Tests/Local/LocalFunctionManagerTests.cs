using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Local;
using SlimFaas.Options;

namespace SlimFaas.Tests.Local;

public sealed class LocalFunctionManagerTests
{
    [Fact]
    public async Task Reconcile_PublishesPortExhaustionThenAllocatesAndReleasesPort()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        int port = FindAvailablePort();
        var manifest = new LocalManifest
        {
            Name = "function-test",
            Cluster = new LocalClusterManifest
            {
                Nodes = 1,
                GatewayPort = port + 10,
                HttpPortBase = port + 11,
                RaftPortBase = port + 12
            },
            ProcessPorts = new LocalPortRangeManifest { From = port, To = port },
            Functions = new Dictionary<string, LocalFunctionManifest>
            {
                ["test-function"] = new LocalFunctionManifest
                {
                    Command =
                    [
                        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                        "--info"
                    ],
                    WorkingDirectory = root,
                    Annotations = new Dictionary<string, string>
                    {
                        [FunctionAnnotationNames.Function] = "true",
                        [FunctionAnnotationNames.ReplicasAtStart] = "1",
                        [FunctionAnnotationNames.Scale] = """{"ReplicaMax":1}"""
                    }
                }
            }
        };
        var loaded = new LoadedLocalManifest(
            manifest,
            Path.Combine(root, "manifest.yaml"),
            root,
            statePath,
            IsPersistent: true);

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalFunctionManager(loaded, allocator, state);
            using var external = new TcpListener(IPAddress.Loopback, port);
            external.Start();
            await manager.StartAsync(CancellationToken.None);

            PodInformation exhausted = await WaitForPodAsync(
                manager,
                pod => pod.StartFailureReason == "PortRangeExhausted");
            Assert.Empty(exhausted.Ports!);

            external.Stop();
            PodInformation allocated = await WaitForPodAsync(
                manager,
                pod => pod.Ports?.Contains(port) == true);
            Assert.Equal(port, Assert.Single(allocated.Ports!));
            Assert.Equal("test-function-0", allocated.RoutingKey);

            await manager.ScaleAsync(
                new ReplicaRequest("test-function", "function-test", 0, PodType.Deployment),
                CancellationToken.None);
            await WaitForAsync(() => manager.Snapshot("function-test").Single().Pods.Count == 0);
            await WaitForAsync(() => !state.Allocations.ContainsKey("function:test-function:0"));
            Assert.DoesNotContain("function:test-function:0", state.Allocations.Keys);

            await manager.ScaleAsync(
                new ReplicaRequest("test-function", "function-test", 1, PodType.Deployment),
                CancellationToken.None);
            PodInformation reallocated = await WaitForPodAsync(
                manager,
                pod => pod.Ports?.Contains(port) == true);
            Assert.Equal(port, Assert.Single(reallocated.Ports!));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DebugFunction_TransitionsStartingRunningStartingWithoutManagingAProcess()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        int port = FindAvailablePort();
        var manifest = new LocalManifest
        {
            Name = "function-test",
            Cluster = new LocalClusterManifest
            {
                Nodes = 1,
                GatewayPort = port + 10,
                HttpPortBase = port + 11,
                RaftPortBase = port + 12
            },
            ProcessPorts = new LocalPortRangeManifest { From = port, To = port },
            Functions = new Dictionary<string, LocalFunctionManifest>
            {
                ["test-function"] = new LocalFunctionManifest
                {
                    Command = [],
                    WorkingDirectory = Path.Combine(root, "does-not-exist"),
                    DebugUrl = $"http://127.0.0.1:{port}/debug",
                    Health = new LocalHealthManifest
                    {
                        Path = "/health",
                        PeriodSeconds = 1,
                        TimeoutSeconds = 1,
                        StartupTimeoutSeconds = 1
                    },
                    Annotations = new Dictionary<string, string>
                    {
                        [FunctionAnnotationNames.Function] = "true",
                        [FunctionAnnotationNames.ReplicasAtStart] = "0",
                        [FunctionAnnotationNames.ReplicasMin] = "0",
                        [FunctionAnnotationNames.Scale] = """{"ReplicaMax":5}"""
                    }
                }
            }
        };
        var loaded = new LoadedLocalManifest(
            manifest,
            Path.Combine(root, "manifest.yaml"),
            root,
            statePath,
            IsPersistent: true);

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalFunctionManager(loaded, allocator, state);
            await manager.StartAsync(CancellationToken.None);

            PodInformation starting = await WaitForPodAsync(
                manager,
                pod => pod.Started == true && pod.Ready == false);
            Assert.Equal("test-function-debug", starting.Name);
            Assert.Equal("test-function-debug", starting.RoutingKey);
            Assert.Equal($"http://127.0.0.1:{port}/debug", starting.EndpointUrl);
            Assert.Equal(port, Assert.Single(starting.Ports!));
            Assert.Empty(manager.PortAllocationIds);
            Assert.Empty(state.Allocations);

            DeploymentInformation deployment = manager.Snapshot("function-test").Single();
            Assert.Equal(1, deployment.Replicas);
            Assert.Equal(1, deployment.ReplicasAtStart);
            Assert.Equal(1, deployment.ReplicasMin);
            Assert.Null(deployment.Scale);

            await using (var server = new DebugHttpServer(port))
            {
                await server.StartAsync();
                PodInformation running = await WaitForPodAsync(
                    manager,
                    pod => pod.Ready == true);
                Assert.True(running.Started);
                await WaitForAsync(() => server.RequestTargets.Contains("/debug/health"));

                var replicasService = new Mock<IReplicasService>();
                replicasService.SetupGet(service => service.Deployments).Returns(
                    () => new DeploymentsInformations(
                        manager.Snapshot("function-test"),
                        new SlimFaasDeploymentInformation(1, []),
                        []));
                var namespaceProvider = new Mock<INamespaceProvider>();
                namespaceProvider.SetupGet(provider => provider.CurrentNamespace)
                    .Returns("function-test");
                using var httpClient = new HttpClient();
                var sendClient = new SendClient(
                    httpClient,
                    new Mock<ILogger<SendClient>>().Object,
                    Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
                    {
                        BaseFunctionUrl = "http://{pod_ip}:{pod_port}",
                        Namespace = "function-test"
                    }),
                    namespaceProvider.Object,
                    new SlimFaas.Endpoints.NetworkActivityTracker());
                var proxy = new Proxy(replicasService.Object, "test-function");
                using HttpResponseMessage response = await sendClient.SendHttpRequestAsync(
                    new CustomRequest(
                        [new CustomHeader("X-Debug-Test", ["true"])],
                        Encoding.UTF8.GetBytes("debug-body"),
                        "test-function",
                        "/calculate",
                        "POST",
                        "?value=8"),
                    new SlimFaasDefaultConfiguration(),
                    proxy: proxy,
                    reservedPodIp: "test-function-debug");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                await WaitForAsync(
                    () => server.RequestTargets.Contains("/debug/calculate?value=8"));
            }

            PodInformation unavailableAgain = await WaitForPodAsync(
                manager,
                pod => pod.Started == true && pod.Ready == false);
            Assert.Null(unavailableAgain.AppFailureReason);
            Assert.Null(unavailableAgain.AppFailureMessage);

            ReplicaRequest? scaleResult = await manager.ScaleAsync(
                new ReplicaRequest("test-function", "function-test", 0, PodType.Deployment),
                CancellationToken.None);
            Assert.Equal(1, scaleResult?.Replicas);
            await Task.Delay(600);
            Assert.Single(manager.Snapshot("function-test").Single().Pods);
            Assert.Empty(state.Allocations);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<PodInformation> WaitForPodAsync(
        LocalFunctionManager manager,
        Func<PodInformation, bool> predicate)
    {
        PodInformation? last = null;
        await WaitForAsync(() =>
        {
            last = manager.Snapshot("function-test").Single().Pods.SingleOrDefault();
            return last is not null && predicate(last);
        });
        return last!;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("Condition was not reached before the timeout.");
    }

    private static int FindAvailablePort()
    {
        for (var port = 44000; port < 60000; port += 20)
        {
            if (Enumerable.Range(port, 13).All(LocalManifestLoader.IsTcpPortAvailable))
                return port;
        }

        throw new InvalidOperationException("No free local port range was found for the test.");
    }

    private sealed class DebugHttpServer(int port) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, port);
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<string> _requestTargets = [];
        private Task? _loop;

        public IReadOnlyList<string> RequestTargets
        {
            get
            {
                lock (_requestTargets)
                    return _requestTargets.ToArray();
            }
        }

        public Task StartAsync()
        {
            _listener.Start();
            _loop = ServeAsync(_stopping.Token);
            return Task.CompletedTask;
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    await using NetworkStream stream = client.GetStream();
                    byte[] requestBuffer = new byte[4096];
                    int read = await stream.ReadAsync(requestBuffer, cancellationToken);
                    string request = Encoding.ASCII.GetString(requestBuffer, 0, read);
                    string? target = request.Split("\r\n", StringSplitOptions.None)
                        .FirstOrDefault()?
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .ElementAtOrDefault(1);
                    if (target is not null)
                    {
                        lock (_requestTargets)
                            _requestTargets.Add(target);
                    }

                    byte[] response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                    await stream.WriteAsync(response, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            if (_loop is not null)
                await _loop;
            _stopping.Dispose();
        }
    }
}
