using System.Reflection;
using k8s;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Kubernetes.Watch;
using SlimFaas.Options;

namespace SlimFaas.Tests.Integration;

/// <summary>
/// The real synchronization stack (KubernetesService + ReplicasService + JobService +
/// JobConfiguration + the three sync workers, plus the watcher when enabled), wired
/// against a <see cref="FakeKubernetesCluster"/>. Used by the end-to-end
/// non-regression test and by the benchmark comparing polling and watch modes.
/// </summary>
public sealed class KubernetesSyncStack : IAsyncDisposable
{
    public const string Namespace = "integration";

    private readonly k8s.Kubernetes _client;
    private readonly List<IHostedService> _workers = new();

    public KubernetesService KubernetesService { get; }
    public ReplicasService ReplicasService { get; }
    public JobService JobService { get; }
    public JobConfiguration JobConfiguration { get; }
    public KubernetesWatchSignals Signals { get; }

    public KubernetesSyncStack(
        FakeKubernetesCluster cluster,
        bool watchEnabled,
        int pollingCadenceMilliseconds,
        int resyncSeconds = 3600,
        int debounceMilliseconds = 50)
    {
        _client = new k8s.Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost" },
            cluster.Handler);

        KubernetesService = (KubernetesService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(KubernetesService));
        typeof(KubernetesService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(KubernetesService, _client);
        typeof(KubernetesService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(KubernetesService, NullLogger<KubernetesService>.Instance);

        var slimFaasOptions = new SlimFaasOptions
        {
            KubernetesWatch = new KubernetesWatchOptions
            {
                Enabled = watchEnabled,
                FunctionsResyncSeconds = resyncSeconds,
                JobsResyncSeconds = resyncSeconds,
                JobsConfigurationResyncSeconds = resyncSeconds,
                DebounceMilliseconds = debounceMilliseconds,
                ReconnectInitialDelayMilliseconds = 100,
                ReconnectMaxDelayMilliseconds = 1000
            }
        };
        var slimFaasOptionsWrapper = Microsoft.Extensions.Options.Options.Create(slimFaasOptions);
        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            ReplicasSynchronizationDelayMilliseconds = pollingCadenceMilliseconds,
            JobsDelayMilliseconds = pollingCadenceMilliseconds,
            JobsConfigurationDelayMilliseconds = pollingCadenceMilliseconds
        });

        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(n => n.CurrentNamespace).Returns(Namespace);

        Signals = new KubernetesWatchSignals { WatchEnabled = watchEnabled };

        ReplicasService = new ReplicasService(
            KubernetesService,
            new HistoryHttpMemoryService(),
            autoScaler: null!, // jamais utilisé : CheckScaleAsync n'est pas exécuté ici
            NullLogger<ReplicasService>.Instance,
            new Mock<IRequestedMetricsRegistry>().Object,
            slimFaasOptionsWrapper);

        JobConfiguration = new JobConfiguration(
            slimFaasOptionsWrapper,
            KubernetesService,
            NullLogger<JobConfiguration>.Instance,
            namespaceProvider.Object);

        JobService = new JobService(
            KubernetesService,
            JobConfiguration,
            new Mock<IJobQueue>().Object,
            namespaceProvider.Object,
            NullLogger<JobService>.Instance);

        var slimDataStatus = new Mock<SlimFaas.Database.ISlimDataStatus>();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);
        var masterService = new Mock<IMasterService>();
        masterService.SetupGet(m => m.IsMaster).Returns(false);

        _workers.Add(new ReplicasSynchronizationWorker(
            ReplicasService,
            NullLogger<ReplicasSynchronizationWorker>.Instance,
            workersOptions,
            slimFaasOptionsWrapper,
            Signals,
            namespaceProvider.Object));
        _workers.Add(new SlimJobsWorker(
            new Mock<IJobQueue>().Object,
            JobService,
            JobConfiguration,
            NullLogger<SlimJobsWorker>.Instance,
            new HistoryHttpMemoryService(),
            slimDataStatus.Object,
            masterService.Object,
            ReplicasService,
            workersOptions,
            slimFaasOptionsWrapper,
            Signals));
        _workers.Add(new SlimJobsConfigurationWorker(
            JobConfiguration,
            NullLogger<SlimJobsConfigurationWorker>.Instance,
            workersOptions,
            slimFaasOptionsWrapper,
            Signals));

        if (watchEnabled)
        {
            _workers.Add(new KubernetesWatcherWorker(
                KubernetesService,
                Signals,
                slimFaasOptionsWrapper,
                namespaceProvider.Object,
                NullLogger<KubernetesWatcherWorker>.Instance));
        }
    }

    public async Task StartAsync()
    {
        // Miroir du démarrage réel (Program.cs) : une synchronisation initiale avant
        // le lancement des workers.
        await ReplicasService.SyncDeploymentsAsync(Namespace);
        foreach (IHostedService worker in _workers)
        {
            await worker.StartAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHostedService worker in _workers)
        {
            try
            {
                await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Teardown : les timeouts d'arrêt ne doivent pas masquer le résultat du test.
            }
        }

        _client.Dispose();
    }

    public static async Task WaitUntilAsync(Func<bool> predicate, string description, int timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Convergence timeout: {description}");
    }
}
