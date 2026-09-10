using System.Net;
using System.Reflection;
using System.Text;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Kubernetes.Watch;
using SlimFaas.Options;

namespace SlimFaas.Tests.Kubernetes;

// Reproduces the review finding at the HTTP-handler boundary: a real Kubernetes
// HTTP failure does not throw out of the sync services (ListFunctionsAsync returns
// the previous snapshot, ListJobsConfigurationAsync returns null), so the workers
// must not commit those synchronizations as successful — a consumed watch pulse
// must be retried at the legacy cadence, not after the 30-60 s safety-net resync.
// Mocks that throw directly from the service do not cover this behavior.
public sealed class WatchSyncHttpBoundaryTests
{
    private static readonly TimeSpan AssertTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FunctionsSyncFailingAtTheHttpBoundaryRetriesAtTheLegacyCadence()
    {
        var handler = new FailingListHandler();
        KubernetesService kubernetesService = BuildKubernetesService(handler, out k8s.Kubernetes client);
        using (client)
        {
            var replicasService = new ReplicasService(
                kubernetesService,
                new HistoryHttpMemoryService(),
                autoScaler: null!, // jamais utilisé : CheckScaleAsync n'est pas exécuté ici
                NullLogger<ReplicasService>.Instance,
                new Mock<IRequestedMetricsRegistry>().Object,
                Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()));

            var signals = new KubernetesWatchSignals { WatchEnabled = true };
            signals.MarkAllStreamsConnected();
            var namespaceProvider = new Mock<INamespaceProvider>();
            namespaceProvider.SetupGet(n => n.CurrentNamespace).Returns("boundary");

            var worker = new ReplicasSynchronizationWorker(
                replicasService,
                NullLogger<ReplicasSynchronizationWorker>.Instance,
                Microsoft.Extensions.Options.Options.Create(new WorkersOptions
                {
                    ReplicasSynchronizationDelayMilliseconds = 50
                }),
                Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
                {
                    KubernetesWatch = new KubernetesWatchOptions { FunctionsResyncSeconds = 3600 }
                }),
                signals,
                namespaceProvider.Object);

            await worker.StartAsync(CancellationToken.None);
            try
            {
                signals.Functions.Pulse();
                // Un LIST en échec (HTTP 500) ne doit pas consommer l'événement pour
                // 3600 s : le retry doit suivre la cadence historique (50 ms).
                await WaitUntilAsync(
                    () => handler.CountOf("deployments") >= 3,
                    "failed functions LISTs retried at the legacy cadence");
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task JobsConfigurationSyncFailingAtTheHttpBoundaryRetriesAtTheLegacyCadence()
    {
        var handler = new FailingListHandler();
        KubernetesService kubernetesService = BuildKubernetesService(handler, out k8s.Kubernetes client);
        using (client)
        {
            var namespaceProvider = new Mock<INamespaceProvider>();
            namespaceProvider.SetupGet(n => n.CurrentNamespace).Returns("boundary");
            var jobConfiguration = new JobConfiguration(
                Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()),
                kubernetesService,
                NullLogger<JobConfiguration>.Instance,
                namespaceProvider.Object);

            var signals = new KubernetesWatchSignals { WatchEnabled = true };
            signals.MarkAllStreamsConnected();

            var worker = new SlimJobsConfigurationWorker(
                jobConfiguration,
                NullLogger<SlimJobsConfigurationWorker>.Instance,
                Microsoft.Extensions.Options.Options.Create(new WorkersOptions
                {
                    JobsConfigurationDelayMilliseconds = 50
                }),
                Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
                {
                    KubernetesWatch = new KubernetesWatchOptions { JobsConfigurationResyncSeconds = 3600 }
                }),
                signals);

            await worker.StartAsync(CancellationToken.None);
            try
            {
                signals.JobsConfiguration.Pulse();
                // Un LIST CronJob en échec (HTTP 500, configuration null) ne doit pas
                // être validé : le retry doit suivre la cadence historique (50 ms).
                await WaitUntilAsync(
                    () => handler.CountOf("cronjobs") >= 3,
                    "failed cronjob LISTs retried at the legacy cadence");
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
    }

    private static KubernetesService BuildKubernetesService(DelegatingHandler handler, out k8s.Kubernetes client)
    {
        client = new k8s.Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost" },
            handler);
        var kubernetesService = (KubernetesService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(KubernetesService));
        typeof(KubernetesService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(kubernetesService, client);
        typeof(KubernetesService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(kubernetesService, NullLogger<KubernetesService>.Instance);
        return kubernetesService;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string description)
    {
        DateTime deadline = DateTime.UtcNow.Add(AssertTimeout);
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

    // Toutes les requêtes échouent en HTTP 500, comptées par ressource : le vrai
    // client Kubernetes transforme ce statut en HttpOperationException, que les
    // services de synchronisation avalent (snapshot précédent / null).
    private sealed class FailingListHandler : DelegatingHandler
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public int CountOf(string resource)
        {
            lock (_gate)
            {
                return _counts.GetValueOrDefault(resource);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string resource = path[(path.LastIndexOf('/') + 1)..];
            lock (_gate)
            {
                _counts[resource] = _counts.GetValueOrDefault(resource) + 1;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"kind\":\"Status\",\"code\":500}", Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
