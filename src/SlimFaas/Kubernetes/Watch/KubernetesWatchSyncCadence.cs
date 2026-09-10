namespace SlimFaas.Kubernetes.Watch;

/// <summary>
/// Cadence partagée des workers de synchronisation pilotés par les signaux watch.
/// Encapsule la politique commune : synchronisation déclenchée par les événements
/// avec resync de sécurité quand les flux sont sains, retour au Task.Delay
/// historique sans watch ou tant qu'un flux est indisponible, et retry à la cadence
/// historique après une synchronisation en échec — la version observée n'est validée
/// que par <see cref="CommitSync"/> après un succès, un événement consommé pendant
/// une synchronisation qui échoue n'attend donc jamais le resync complet.
/// Deux modes d'utilisation : attente bloquante (<see cref="WaitForSyncDueAsync"/>)
/// pour les workers dont la boucle est pilotée par le signal, ou interrogation
/// (<see cref="IsSyncDue"/>) depuis une boucle qui garde sa propre cadence.
/// Non thread-safe : une instance appartient à un seul worker.
/// </summary>
public sealed class KubernetesWatchSyncCadence
{
    private readonly KubernetesWatchSignals _signals;
    private readonly KubernetesResourceSignal _signal;
    private readonly TimeSpan _legacyDelay;
    private readonly TimeSpan _resyncInterval;
    private readonly TimeProvider _timeProvider;

    // Version observée capturée à la construction, donc avant le démarrage de tout
    // hosted service : le watcher ne peut pas encore avoir pulsé.
    private long _observedVersion;
    private long _pendingVersion;
    private long _lastSyncTimestamp;
    private bool _hasSynced;
    private bool _forceNextSync;
    private bool _lastSyncFailed;

    public KubernetesWatchSyncCadence(
        KubernetesWatchSignals signals,
        KubernetesResourceSignal signal,
        TimeSpan legacyDelay,
        TimeSpan resyncInterval,
        TimeProvider? timeProvider = null)
    {
        _signals = signals;
        _signal = signal;
        _legacyDelay = legacyDelay;
        _resyncInterval = resyncInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observedVersion = signal.Version;
        _pendingVersion = _observedVersion;
    }

    private bool EventDriven => _signals.WatchEnabled && _signal.IsHealthy;

    // Après un échec, l'attente retombe sur la cadence historique : la donnée est
    // périmée et aucun nouvel événement n'est garanti pour relancer la tentative.
    private TimeSpan MaximumWait => EventDriven && !_lastSyncFailed ? _resyncInterval : _legacyDelay;

    /// <summary>
    /// Attend qu'une synchronisation soit due : un pulse du signal, ou l'expiration
    /// du resync de sécurité (cadence historique sans watch, flux indisponible ou
    /// après un échec). Appeler <see cref="CommitSync"/> après une synchronisation
    /// réussie, <see cref="MarkSyncFailed"/> après un échec.
    /// </summary>
    public async Task WaitForSyncDueAsync(CancellationToken cancellationToken) =>
        _pendingVersion = await _signal.WaitForChangeAsync(_observedVersion, MaximumWait, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Variante interrogée depuis une boucle cadencée : true si un événement est
    /// arrivé depuis la dernière synchronisation validée, si le resync de sécurité
    /// est échu, si <see cref="ForceNextSync"/> a été demandé, ou tant que les
    /// événements ne sont pas fiables (watch désactivé, flux indisponible, première
    /// synchronisation, échec précédent non validé).
    /// </summary>
    public bool IsSyncDue()
    {
        _pendingVersion = _signal.Version;
        if (!_hasSynced || _forceNextSync || !EventDriven || _pendingVersion != _observedVersion)
        {
            return true;
        }

        return _timeProvider.GetElapsedTime(_lastSyncTimestamp) >= _resyncInterval;
    }

    /// <summary>Valide une synchronisation réussie : les événements consommés sont acquis.</summary>
    public void CommitSync()
    {
        _observedVersion = _pendingVersion;
        _hasSynced = true;
        _forceNextSync = false;
        _lastSyncFailed = false;
        _lastSyncTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Signale une synchronisation en échec : la version consommée est acquise (pour
    /// ne pas boucler à chaud sur un échec persistant) mais la prochaine attente
    /// retombe sur la cadence historique jusqu'au prochain succès.
    /// </summary>
    public void MarkSyncFailed()
    {
        _observedVersion = _pendingVersion;
        _lastSyncFailed = true;
    }

    /// <summary>
    /// Force la prochaine <see cref="IsSyncDue"/> à répondre true : à appeler après
    /// une écriture (création de jobs) dont les événements watch peuvent arriver
    /// après le prochain cycle — la liste doit être relue avant tout raisonnement
    /// read-after-write.
    /// </summary>
    public void ForceNextSync() => _forceNextSync = true;
}
