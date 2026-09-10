using System.Net;
using System.Reflection;
using System.Threading.Channels;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Kubernetes.Watch;
using SlimFaas.Options;

namespace SlimFaas.Tests.Kubernetes;

// Tests of the watch stream loop: pulses on change events (debounced), resourceVersion
// continuity across reconnects, 410 handling, and forced pulse after an errored
// reconnect. The loop is driven directly (internal RunWatchLoopAsync) against a fake
// handler streaming watch JSON lines.
public sealed class KubernetesWatcherWorkerTests
{
    private static readonly TimeSpan AssertTimeout = TimeSpan.FromSeconds(10);

    private static KubernetesWatchOptions FastOptions() => new()
    {
        DebounceMilliseconds = 50,
        ReconnectInitialDelayMilliseconds = 30,
        ReconnectMaxDelayMilliseconds = 100,
        WatchTimeoutSeconds = 60
    };

    [Fact]
    public async Task ChangeEventPulsesTheSignalAfterDebounce()
    {
        using var harness = new WatchHarness(FastOptions());
        long observed = harness.Signal.Version;

        harness.WriteLine("{\"type\":\"ADDED\",\"object\":{\"metadata\":{\"resourceVersion\":\"10\"}}}");

        long version = await harness.Signal
            .WaitForChangeAsync(observed, TimeSpan.FromSeconds(5), CancellationToken.None)
            .WaitAsync(AssertTimeout);
        Assert.True(version > observed);
    }

    [Fact]
    public async Task BurstOfEventsCoalescesIntoASinglePulse()
    {
        using var harness = new WatchHarness(FastOptions());
        long observed = harness.Signal.Version;

        for (var i = 0; i < 10; i++)
        {
            harness.WriteLine($"{{\"type\":\"MODIFIED\",\"object\":{{\"metadata\":{{\"resourceVersion\":\"{20 + i}\"}}}}}}");
        }

        long version = await harness.Signal
            .WaitForChangeAsync(observed, TimeSpan.FromSeconds(5), CancellationToken.None)
            .WaitAsync(AssertTimeout);
        // Attendre au-delà de la fenêtre de debounce pour capturer un éventuel 2e pulse.
        await Task.Delay(300);

        Assert.Equal(version, harness.Signal.Version);
        Assert.Equal(observed + 1, version);
    }

    [Fact]
    public async Task BookmarkAloneDoesNotPulseButAdvancesResourceVersion()
    {
        using var harness = new WatchHarness(FastOptions());
        long observed = harness.Signal.Version;

        harness.WriteLine("{\"type\":\"BOOKMARK\",\"object\":{\"metadata\":{\"resourceVersion\":\"77\"}}}");
        await Task.Delay(300);
        Assert.Equal(observed, harness.Signal.Version);

        // Fin de flux : la reconnexion doit reprendre à la resourceVersion du bookmark.
        harness.CompleteStream();
        string secondRequest = await harness.WaitForRequestAsync(2);
        Assert.Contains("resourceVersion=77", secondRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http410ClearsResourceVersionAndPulses()
    {
        var options = FastOptions();
        using var harness = new WatchHarness(options, firstResponseStatus: HttpStatusCode.Gone);
        // Version initiale du signal : le pulse du 410 peut survenir avant toute
        // lecture, on observe donc depuis 0.
        const long observed = 0;

        // Le 410 doit pulser (resync) puis se reconnecter SANS resourceVersion.
        long version = await harness.Signal
            .WaitForChangeAsync(observed, TimeSpan.FromSeconds(5), CancellationToken.None)
            .WaitAsync(AssertTimeout);
        Assert.True(version > observed);

        string secondRequest = await harness.WaitForRequestAsync(2);
        Assert.DoesNotContain("resourceVersion=", secondRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedConnectionForcesAPulseOnSuccessfulReconnect()
    {
        using var harness = new WatchHarness(FastOptions(), firstResponseStatus: HttpStatusCode.InternalServerError);

        // Aucun événement écrit : le flux démarre en dette de connexion (pas de pulse
        // au signalement de la panne initiale), le pulse vient de la reconnexion
        // réussie après échec (des événements ont pu être manqués pendant la coupure).
        await harness.WaitForRequestAsync(2);
        await WatchHarness.WaitUntilAsync(() => harness.Signal.Version >= 1, "pulse after successful reconnect");
        Assert.True(harness.Signal.IsHealthy);
    }

    [Fact]
    public async Task PersistentFailureReportsTheSignalUnhealthyUntilTheStreamIsRestored()
    {
        // 3 refus consécutifs (ex. RBAC sans verbe "watch"), puis le flux s'ouvre.
        using var harness = new WatchHarness(FastOptions(), firstResponseStatus: HttpStatusCode.Forbidden, failureResponses: 3);

        await WatchHarness.WaitUntilAsync(() => !harness.Signal.IsHealthy, "signal reported unhealthy");

        await harness.WaitForRequestAsync(4);
        await WatchHarness.WaitUntilAsync(() => harness.Signal.IsHealthy, "signal healthy after reconnect");
        // Pulse forcé après la reconnexion réussie.
        await WatchHarness.WaitUntilAsync(() => harness.Signal.Version >= 1, "pulse after reconnect");

        // Exactement 1 pulse sur toute la séquence : celui de la reconnexion (la
        // panne initiale ne pulse pas, le flux démarre en dette de connexion et les
        // consommateurs sont déjà sur la cadence historique). Les tentatives
        // intermédiaires ne re-pulsent pas (pas de resync en rafale).
        await Task.Delay(300);
        Assert.Equal(1, harness.Signal.Version);
    }

    private sealed class WatchHarness : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly StreamingHandler _handler;
        private readonly k8s.Kubernetes _client;
        private readonly Task _loop;

        public KubernetesResourceSignal Signal { get; } = new();

        public WatchHarness(
            KubernetesWatchOptions options,
            HttpStatusCode? firstResponseStatus = null,
            int failureResponses = 1)
        {
            // Comme à l'activation du watch : le flux est attendu, le signal reste
            // indisponible tant que la connexion n'est pas établie.
            Signal.ExpectStream();
            _handler = new StreamingHandler(firstResponseStatus, failureResponses);
            _client = new k8s.Kubernetes(
                new KubernetesClientConfiguration { Host = "http://localhost" },
                _handler);

            var kubernetesService = (KubernetesService)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(KubernetesService));
            typeof(KubernetesService)
                .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(kubernetesService, _client);
            typeof(KubernetesService)
                .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(kubernetesService, NullLogger<KubernetesService>.Instance);

            var signals = new KubernetesWatchSignals { WatchEnabled = true };
            var namespaceProvider = new Mock<INamespaceProvider>();
            namespaceProvider.SetupGet(n => n.CurrentNamespace).Returns("test");
            var worker = new KubernetesWatcherWorker(
                kubernetesService,
                signals,
                Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions { KubernetesWatch = options }),
                namespaceProvider.Object,
                NullLogger<KubernetesWatcherWorker>.Instance);

            var target = new KubernetesWatcherWorker.WatchTarget(
                "pods",
                "api/v1/namespaces/test/pods",
                [Signal]);
            _loop = worker.RunWatchLoopAsync(
                _client,
                target,
                [new KubernetesWatcherWorker.DebounceChannel(Signal)],
                options,
                _cts.Token);
        }

        public int RequestCount => _handler.Requests.Count;

        public void WriteLine(string line) => _handler.WriteLine(line);

        public void CompleteStream() => _handler.CompleteCurrentStream();

        public async Task<string> WaitForRequestAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_handler.Requests)
                {
                    if (_handler.Requests.Count >= count)
                    {
                        return _handler.Requests[count - 1];
                    }
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"Expected at least {count} watch requests, got {RequestCount}.");
        }

        public static async Task WaitUntilAsync(Func<bool> predicate, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"Condition not met: {description}.");
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loop.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Fin de boucle : les annulations/timeouts de teardown sont attendus.
            }

            _client.Dispose();
            _cts.Dispose();
        }
    }

    private sealed class StreamingHandler(HttpStatusCode? firstResponseStatus, int failureResponses) : DelegatingHandler
    {
        private readonly HttpStatusCode? _failureStatus = firstResponseStatus;
        private int _remainingFailures = failureResponses;
        private WatchStreamContent? _currentContent;

        public List<string> Requests { get; } = new();

        public void WriteLine(string line) => _currentContent?.WriteLine(line);

        public void CompleteCurrentStream() => _currentContent?.Complete();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.PathAndQuery);
            }

            if (_failureStatus is { } failure && _remainingFailures > 0)
            {
                _remainingFailures--;
                return Task.FromResult(new HttpResponseMessage(failure) { RequestMessage = request });
            }

            var content = new WatchStreamContent();
            _currentContent = content;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }

    // HttpContent dont le flux de lecture est branché sur un Pipe : les lignes écrites
    // par le test sont visibles immédiatement côté lecteur (pas de bufferisation).
    private sealed class WatchStreamContent : HttpContent
    {
        private readonly System.IO.Pipelines.Pipe _pipe = new();

        public void WriteLine(string line)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            _ = _pipe.Writer.WriteAsync(bytes).AsTask();
        }

        public void Complete() => _pipe.Writer.Complete();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _pipe.Reader.CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(_pipe.Reader.AsStream());

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
