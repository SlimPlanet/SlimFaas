using SlimFaas.Kubernetes.Watch;

namespace SlimFaas.Tests.Kubernetes;

// Tests of the shared watch/fallback cadence policy: minimum spacing between
// event-driven syncs under sustained churn, legacy-cadence retry after a failed
// sync, and the connection debt that keeps signals unhealthy until the watcher
// actually connects the streams.
public class KubernetesWatchSyncCadenceTests
{
    [Fact]
    public async Task EventDrivenSyncsAreSpacedByTheLegacyCadenceUnderSustainedChurn()
    {
        var signals = new KubernetesWatchSignals { WatchEnabled = true };
        signals.MarkAllStreamsConnected();
        var cadence = new KubernetesWatchSyncCadence(
            signals,
            signals.Functions,
            legacyDelay: TimeSpan.FromMilliseconds(300),
            resyncInterval: TimeSpan.FromSeconds(3600));

        // Première synchronisation : aucun espacement (rien n'a encore été validé).
        signals.Functions.Pulse();
        await cadence.WaitForSyncDueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        cadence.CommitSync();

        // Pulse immédiat après la validation : la synchronisation suivante doit être
        // espacée d'au moins la cadence historique (marge pour la précision des timers).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        signals.Functions.Pulse();
        await cadence.WaitForSyncDueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds >= 150,
            $"Expected at least 150 ms spacing between event-driven syncs, got {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AConsumedPulseIsRetriedAtTheLegacyCadenceWhenTheSyncFails()
    {
        var signals = new KubernetesWatchSignals { WatchEnabled = true };
        signals.MarkAllStreamsConnected();
        var cadence = new KubernetesWatchSyncCadence(
            signals,
            signals.Functions,
            legacyDelay: TimeSpan.FromMilliseconds(50),
            resyncInterval: TimeSpan.FromSeconds(3600));

        // Un pulse consommé par une synchronisation qui échoue...
        signals.Functions.Pulse();
        await cadence.WaitForSyncDueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        cadence.MarkSyncFailed();

        // ... est retenté à la cadence historique (50 ms), pas au resync de sécurité
        // (3600 s) : l'attente suivante doit aboutir sans aucun nouveau pulse.
        await cadence.WaitForSyncDueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        cadence.CommitSync();
    }

    [Fact]
    public void SignalsStartUnhealthyWhenWatchIsEnabledUntilStreamsConnect()
    {
        // Watch désactivé : pas de dette de connexion, les signaux sont sains (les
        // workers sont de toute façon sur leur cadence historique).
        var disabled = new KubernetesWatchSignals();
        Assert.True(disabled.Functions.IsHealthy);
        Assert.True(disabled.Jobs.IsHealthy);
        Assert.True(disabled.JobsConfiguration.IsHealthy);

        // Watch activé : « watcher jamais connecté » doit être indistinguable d'un
        // flux en panne — les workers restent sur la cadence historique.
        var enabled = new KubernetesWatchSignals { WatchEnabled = true };
        Assert.False(enabled.Functions.IsHealthy);
        Assert.False(enabled.Jobs.IsHealthy);
        Assert.False(enabled.JobsConfiguration.IsHealthy);

        enabled.MarkAllStreamsConnected();
        Assert.True(enabled.Functions.IsHealthy);
        Assert.True(enabled.Jobs.IsHealthy);
        Assert.True(enabled.JobsConfiguration.IsHealthy);
    }
}
