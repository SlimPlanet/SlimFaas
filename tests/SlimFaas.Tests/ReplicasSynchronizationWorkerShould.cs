using System.Collections;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests;

public class DeploymentsTestData : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return new object[]
        {
            new DeploymentsInformations(new List<DeploymentInformation>(),
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()), new List<PodInformation>())
        };
        yield return new object[]
        {
            new DeploymentsInformations(
                new List<DeploymentInformation>
                {
                    new("fibonacci1", "default", Replicas: 1, Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration()),
                    new("fibonacci2", "default", Replicas: 0, Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration())
                },
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
                new List<PodInformation>()
            )
        };
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// Live tests for the watch-driven synchronization behavior of
// ReplicasSynchronizationWorker: legacy cadence when watch is disabled, pulse-driven
// sync when enabled, resync fallback, and error resilience.
public class ReplicasSynchronizationWorkerShould
{
    private static readonly TimeSpan AssertTimeout = TimeSpan.FromSeconds(10);

    private static (ReplicasSynchronizationWorker Worker, Mock<IReplicasService> ReplicasService, TaskCompletionSource SyncCalled)
        CreateWorker(SlimFaas.Kubernetes.Watch.KubernetesWatchSignals signals, int legacyDelayMs, int resyncSeconds)
    {
        var replicasService = new Mock<IReplicasService>();
        var syncCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replicasService
            .Setup(r => r.SyncDeploymentsAsync(It.IsAny<string>()))
            .Callback(() => syncCalled.TrySetResult())
            .ReturnsAsync(new DeploymentsInformations(
                new List<DeploymentInformation>(),
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
                new List<PodInformation>()));

        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(n => n.CurrentNamespace).Returns("unit-test");

        var worker = new ReplicasSynchronizationWorker(
            replicasService.Object,
            new Mock<ILogger<ReplicasSynchronizationWorker>>().Object,
            Microsoft.Extensions.Options.Options.Create(new WorkersOptions
            {
                ReplicasSynchronizationDelayMilliseconds = legacyDelayMs
            }),
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                KubernetesWatch = new KubernetesWatchOptions { FunctionsResyncSeconds = resyncSeconds }
            }),
            signals,
            namespaceProvider.Object);
        return (worker, replicasService, syncCalled);
    }

    [Fact]
    public async Task SyncOnLegacyCadenceWhenWatchIsDisabled()
    {
        var signals = new SlimFaas.Kubernetes.Watch.KubernetesWatchSignals { WatchEnabled = false };
        var (worker, replicasService, syncCalled) = CreateWorker(signals, legacyDelayMs: 50, resyncSeconds: 3600);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await syncCalled.Task.WaitAsync(AssertTimeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        replicasService.Verify(r => r.SyncDeploymentsAsync("unit-test"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SyncImmediatelyWhenTheFunctionsSignalPulses()
    {
        var signals = new SlimFaas.Kubernetes.Watch.KubernetesWatchSignals { WatchEnabled = true };
        // Flux watch tous connectés : le worker est en mode event-driven.
        signals.MarkAllStreamsConnected();
        // Resync très long : seule une impulsion peut déclencher la synchronisation.
        var (worker, replicasService, syncCalled) = CreateWorker(signals, legacyDelayMs: 10, resyncSeconds: 3600);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            replicasService.Verify(r => r.SyncDeploymentsAsync(It.IsAny<string>()), Times.Never);

            signals.Functions.Pulse();
            await syncCalled.Task.WaitAsync(AssertTimeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        replicasService.Verify(r => r.SyncDeploymentsAsync("unit-test"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SyncOnResyncFallbackWithoutAnyPulse()
    {
        var signals = new SlimFaas.Kubernetes.Watch.KubernetesWatchSignals { WatchEnabled = true };
        signals.MarkAllStreamsConnected();
        // Resync de 1 s : la synchronisation doit se produire sans aucune impulsion.
        var (worker, replicasService, syncCalled) = CreateWorker(signals, legacyDelayMs: 10, resyncSeconds: 1);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await syncCalled.Task.WaitAsync(AssertTimeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        replicasService.Verify(r => r.SyncDeploymentsAsync("unit-test"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task KeepRunningWhenSyncThrows()
    {
        var signals = new SlimFaas.Kubernetes.Watch.KubernetesWatchSignals { WatchEnabled = true };
        signals.MarkAllStreamsConnected();
        var replicasService = new Mock<IReplicasService>();
        int calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replicasService
            .Setup(r => r.SyncDeploymentsAsync(It.IsAny<string>()))
            .Callback(() =>
            {
                if (Interlocked.Increment(ref calls) >= 2)
                {
                    secondCall.TrySetResult();
                }
            })
            .ThrowsAsync(new InvalidOperationException("boom"));

        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(n => n.CurrentNamespace).Returns("unit-test");
        var worker = new ReplicasSynchronizationWorker(
            replicasService.Object,
            new Mock<ILogger<ReplicasSynchronizationWorker>>().Object,
            Microsoft.Extensions.Options.Options.Create(new WorkersOptions()),
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                KubernetesWatch = new KubernetesWatchOptions { FunctionsResyncSeconds = 1 }
            }),
            signals,
            namespaceProvider.Object);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await secondCall.Task.WaitAsync(AssertTimeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task FallBackToLegacyCadenceWhileTheWatchStreamIsDown()
    {
        var signals = new SlimFaas.Kubernetes.Watch.KubernetesWatchSignals { WatchEnabled = true };
        signals.MarkAllStreamsConnected();
        // Resync très long : seule la cadence historique peut produire plusieurs syncs.
        var (worker, replicasService, _) = CreateWorker(signals, legacyDelayMs: 20, resyncSeconds: 3600);
        // Flux indisponible (ex. RBAC sans verbe "watch") avant même le démarrage.
        signals.Functions.ReportStreamDown();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(500);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // ~25 cycles possibles en 500 ms à 20 ms : au moins 3 prouvent la cadence legacy.
        replicasService.Verify(r => r.SyncDeploymentsAsync("unit-test"), Times.AtLeast(3));
    }

    [Fact]
    public async Task ReturnToEventDrivenSyncWhenTheWatchStreamIsRestored()
    {
        var signals = new SlimFaas.Kubernetes.Watch.KubernetesWatchSignals { WatchEnabled = true };
        signals.MarkAllStreamsConnected();
        var (worker, replicasService, _) = CreateWorker(signals, legacyDelayMs: 20, resyncSeconds: 3600);
        signals.Functions.ReportStreamDown();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200);
            signals.Functions.ReportStreamUp();
            // Attend la quiescence (un cycle legacy en vol peut encore synchroniser
            // après le rétablissement, avec un retard arbitraire sur un runner CI
            // chargé), puis compte les syncs : sans pulse et avec un resync de
            // 3600 s, plus aucune sync ne doit se produire.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            int observed = replicasService.Invocations.Count;
            DateTime quietSince = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline && DateTime.UtcNow - quietSince < TimeSpan.FromMilliseconds(250))
            {
                await Task.Delay(50);
                int current = replicasService.Invocations.Count;
                if (current != observed)
                {
                    observed = current;
                    quietSince = DateTime.UtcNow;
                }
            }

            replicasService.Invocations.Clear();
            await Task.Delay(300);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        replicasService.Verify(r => r.SyncDeploymentsAsync(It.IsAny<string>()), Times.Never);
    }
}
