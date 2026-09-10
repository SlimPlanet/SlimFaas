using Microsoft.Extensions.Options;
using SlimFaas.Options;

namespace SlimFaas.Kubernetes.Watch;

/// <summary>
/// Opens long-lived Kubernetes watch streams (pods, deployments, statefulsets, jobs,
/// cronjobs) and turns their events into debounced change signals. The signals only
/// tell the synchronization workers "something changed, re-list now" — the existing
/// LIST-based synchronization code runs unchanged, so behavior is identical to
/// polling, just event-driven. Streams reconnect with exponential backoff; after an
/// errored reconnect a pulse is forced so no change can be missed during the gap.
/// While a stream is down (RBAC without the "watch" verb, API server unreachable,
/// ...) its signals are reported unhealthy so the workers fall back to their legacy
/// polling cadence instead of the (slower) safety-net resync.
/// Services are intentionally not watched (RBAC does not grant them).
/// </summary>
public class KubernetesWatcherWorker(
    IKubernetesService kubernetesService,
    KubernetesWatchSignals signals,
    IOptions<SlimFaasOptions> slimFaasOptions,
    INamespaceProvider namespaceProvider,
    ILogger<KubernetesWatcherWorker> logger) : BackgroundService
{
    internal sealed record WatchTarget(string Name, string PathTemplate, KubernetesResourceSignal[] Signals);

    internal sealed class DebounceChannel(KubernetesResourceSignal signal)
    {
        private int _pending;

        public KubernetesResourceSignal Signal { get; } = signal;

        /// <summary>Arms the debounce window; returns false if it is already armed.</summary>
        public bool TryArm() => Interlocked.Exchange(ref _pending, 1) == 0;

        /// <summary>Disarms the debounce window so that the next event re-arms it.</summary>
        public void Disarm() => Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>Outcome of one connected watch stream, once it ended.</summary>
    private enum StreamEnd
    {
        Completed,
        Error
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (kubernetesService is not KubernetesService concreteService)
        {
            logger.LogInformation(
                "KubernetesWatcherWorker disabled: the orchestrator is not the Kubernetes implementation");
            return;
        }

        var options = slimFaasOptions.Value.KubernetesWatch;
        string ns = namespaceProvider.CurrentNamespace;

        var functionsChannel = new DebounceChannel(signals.Functions);
        var jobsChannel = new DebounceChannel(signals.Jobs);
        var jobsConfigurationChannel = new DebounceChannel(signals.JobsConfiguration);

        // Deux watch pods disjoints par sélecteur de label, miroirs des LIST qu'ils
        // déclenchent : les pods de jobs (label slimfaas-job-name, cf. ListJobsAsync)
        // n'alimentent que le canal Jobs, les autres pods que le canal Functions —
        // un cycle de vie de pod ne déclenche ainsi qu'un seul pipeline de re-list.
        // La topologie (nombre de flux par canal) doit rester alignée avec la dette
        // de connexion déclarée dans KubernetesWatchSignals.WatchEnabled.
        string functionPodsSelector = Uri.EscapeDataString($"!{KubernetesService.SlimfaasJobName}");
        string jobPodsSelector = Uri.EscapeDataString(KubernetesService.SlimfaasJobName);
        WatchTarget[] targets =
        [
            new("pods", $"api/v1/namespaces/{ns}/pods?labelSelector={functionPodsSelector}", [signals.Functions]),
            new("job-pods", $"api/v1/namespaces/{ns}/pods?labelSelector={jobPodsSelector}", [signals.Jobs]),
            new("deployments", $"apis/apps/v1/namespaces/{ns}/deployments", [signals.Functions]),
            new("statefulsets", $"apis/apps/v1/namespaces/{ns}/statefulsets", [signals.Functions]),
            new("jobs", $"apis/batch/v1/namespaces/{ns}/jobs", [signals.Jobs]),
            new("cronjobs", $"apis/batch/v1/namespaces/{ns}/cronjobs", [signals.JobsConfiguration])
        ];

        var channelsBySignal = new Dictionary<KubernetesResourceSignal, DebounceChannel>
        {
            [signals.Functions] = functionsChannel,
            [signals.Jobs] = jobsChannel,
            [signals.JobsConfiguration] = jobsConfigurationChannel
        };

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "KubernetesWatcherWorker starting: watching pods/deployments/statefulsets/jobs/cronjobs in namespace {Namespace}",
                ns);
        }

        Task[] loops = targets
            .Select(target => RunWatchLoopAsync(
                concreteService.Client,
                target,
                target.Signals.Select(signal => channelsBySignal[signal]).ToArray(),
                options,
                stoppingToken))
            .ToArray();

        await Task.WhenAll(loops);
    }

    internal async Task RunWatchLoopAsync(
        k8s.Kubernetes client,
        WatchTarget target,
        DebounceChannel[] channels,
        KubernetesWatchOptions options,
        CancellationToken stoppingToken)
    {
        var state = new WatchLoopState(options.ReconnectInitialDelayMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneConnectionAsync(client, target, channels, options, state, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Inclut TaskCanceledException issue du timeout HttpClient (~100 s).
                MarkStreamDown(target, channels, state, ex, statusCode: null);
                try
                {
                    await BackoffAsync(options, state, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        // Arrêt du worker : ne pas laisser les signaux en état dégradé.
        MarkStreamUp(target, channels, state, logRecovery: false);
    }

    // Un flux accepté (2xx) qui se ferme aussitôt sans avoir livré une seule ligne
    // watch valide (proxy/LB sans support du streaming, par exemple) ne prouve rien :
    // en dessous de cette durée de vie, la reconnexion subit le backoff au lieu de
    // boucler à chaud, et au bout de EmptyStreamUnhealthyThreshold fermetures
    // consécutives le flux est signalé indisponible.
    internal const int MinimumStreamLifetimeMilliseconds = 5000;
    internal const int EmptyStreamUnhealthyThreshold = 3;

    private sealed class WatchLoopState(int initialBackoffMilliseconds)
    {
        public string? LastResourceVersion;
        public int BackoffMilliseconds = initialBackoffMilliseconds;
        public bool PreviousAttemptFailed;

        // Le flux démarre avec la dette de connexion déclarée à l'activation du watch
        // (KubernetesResourceSignal.ExpectStream) : la première connexion prouvée la
        // solde via MarkStreamUp, sans log de rétablissement.
        public bool ReportedDown = true;
        public bool WarnedDown;
        public int ConsecutiveEmptyStreams;
        public bool CurrentStreamDelivered;
    }

    private async Task RunOneConnectionAsync(
        k8s.Kubernetes client,
        WatchTarget target,
        DebounceChannel[] channels,
        KubernetesWatchOptions options,
        WatchLoopState state,
        CancellationToken stoppingToken)
    {
        char querySeparator = target.PathTemplate.Contains('?') ? '&' : '?';
        string url = string.Concat(
            client.BaseUri,
            target.PathTemplate,
            $"{querySeparator}watch=true&allowWatchBookmarks=true&timeoutSeconds={options.WatchTimeoutSeconds}");
        if (!string.IsNullOrEmpty(state.LastResourceVersion))
        {
            url += $"&resourceVersion={state.LastResourceVersion}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (client.Credentials != null)
        {
            await client.Credentials.ProcessHttpRequestAsync(request, stoppingToken).ConfigureAwait(false);
        }

        using var response = await client.HttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            // 410 : resourceVersion trop ancienne — trou dans l'historique.
            logger.LogWarning(
                "Watch stream {Target} received HTTP 410 Gone: resetting resourceVersion and signaling a resync",
                target.Name);
            state.LastResourceVersion = null;
            PulseAll(channels);
            await BackoffAsync(options, state, stoppingToken).ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            // 403/404/5xx : les workers retombent sur leur cadence de polling historique
            // tant que le flux n'est pas rétabli.
            MarkStreamDown(target, channels, state, exception: null, (int)response.StatusCode);
            await BackoffAsync(options, state, stoppingToken).ConfigureAwait(false);
            return;
        }

        // Un 2xx ne suffit pas à déclarer le flux sain : tant que les fermetures
        // immédiates s'enchaînent, la santé n'est restaurée qu'à la première ligne
        // watch valide (ou à une fermeture après une durée de vie normale).
        bool suspicious = state.ConsecutiveEmptyStreams >= EmptyStreamUnhealthyThreshold;
        if (!suspicious)
        {
            RestoreStreamHealth(target, channels, options, state);
        }

        state.CurrentStreamDelivered = false;
        long startedAt = Environment.TickCount64;
        await using Stream stream = await response.Content.ReadAsStreamAsync(stoppingToken).ConfigureAwait(false);
        StreamEnd end = await ReadStreamAsync(stream, target, channels, options, state, stoppingToken)
            .ConfigureAwait(false);
        if (end == StreamEnd.Error)
        {
            await BackoffAsync(options, state, stoppingToken).ConfigureAwait(false);
            return;
        }

        long lifetime = Environment.TickCount64 - startedAt;
        if (state.CurrentStreamDelivered || lifetime >= MinimumStreamLifetimeMilliseconds)
        {
            // Fin de flux normale (rotation serveur, y compris sur un namespace
            // inactif) : reconnexion immédiate avec continuité de resourceVersion.
            RestoreStreamHealth(target, channels, options, state);
            return;
        }

        // 2xx refermé aussitôt sans une seule ligne watch : reconnexion sous backoff
        // pour ne pas marteler l'API server, et signalement du flux indisponible au
        // bout de quelques fermetures consécutives.
        state.ConsecutiveEmptyStreams++;
        if (state.ConsecutiveEmptyStreams == EmptyStreamUnhealthyThreshold)
        {
            MarkStreamDown(target, channels, state, exception: null, (int)response.StatusCode);
        }

        await BackoffAsync(options, state, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Première ligne watch valide de la connexion courante : le flux est prouvé
    /// opérationnel, y compris s'il était considéré suspect.
    /// </summary>
    private void OnWatchLineDelivered(
        WatchTarget target,
        DebounceChannel[] channels,
        KubernetesWatchOptions options,
        WatchLoopState state)
    {
        if (state.CurrentStreamDelivered)
        {
            return;
        }

        state.CurrentStreamDelivered = true;
        RestoreStreamHealth(target, channels, options, state);
    }

    /// <summary>
    /// Déclare le flux opérationnel : compteurs et backoff remis à zéro, signal
    /// rétabli, et pulse de rattrapage si des événements ont pu être manqués pendant
    /// une coupure. Idempotent.
    /// </summary>
    private void RestoreStreamHealth(
        WatchTarget target,
        DebounceChannel[] channels,
        KubernetesWatchOptions options,
        WatchLoopState state)
    {
        state.ConsecutiveEmptyStreams = 0;
        state.BackoffMilliseconds = options.ReconnectInitialDelayMilliseconds;
        MarkStreamUp(target, channels, state, logRecovery: true);
        if (state.PreviousAttemptFailed)
        {
            // Des événements ont pu être manqués pendant la coupure.
            state.PreviousAttemptFailed = false;
            PulseAll(channels);
        }
    }

    private async Task<StreamEnd> ReadStreamAsync(
        Stream stream,
        WatchTarget target,
        DebounceChannel[] channels,
        KubernetesWatchOptions options,
        WatchLoopState state,
        CancellationToken stoppingToken)
    {
        using var reader = new StreamReader(stream);
        while (!stoppingToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
            if (line is null)
            {
                // Rotation serveur (timeoutSeconds) : reconnexion avec la RV courante.
                return StreamEnd.Completed;
            }

            WatchEventInfo eventInfo = WatchEventLineParser.Parse(line);
            switch (eventInfo.Kind)
            {
                case WatchEventKind.Change:
                    OnWatchLineDelivered(target, channels, options, state);
                    state.LastResourceVersion = eventInfo.ResourceVersion ?? state.LastResourceVersion;
                    SignalDirty(channels, options, stoppingToken);
                    break;
                case WatchEventKind.Bookmark:
                    OnWatchLineDelivered(target, channels, options, state);
                    state.LastResourceVersion = eventInfo.ResourceVersion ?? state.LastResourceVersion;
                    break;
                case WatchEventKind.Error:
                    if (eventInfo.ErrorCode == 410)
                    {
                        state.LastResourceVersion = null;
                    }
                    logger.LogWarning(
                        "Watch stream {Target} received an ERROR event (code {Code}): reconnecting",
                        target.Name,
                        eventInfo.ErrorCode);
                    PulseAll(channels);
                    return StreamEnd.Error;
                default:
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug("Watch stream {Target}: ignoring unknown line", target.Name);
                    }

                    break;
            }
        }

        return StreamEnd.Completed;
    }

    private static async Task BackoffAsync(
        KubernetesWatchOptions options,
        WatchLoopState state,
        CancellationToken stoppingToken)
    {
        int delay = state.BackoffMilliseconds;
        state.BackoffMilliseconds = Math.Min(state.BackoffMilliseconds * 2, options.ReconnectMaxDelayMilliseconds);
        // Jitter ±20 % pour éviter les reconnexions synchronisées entre replicas.
        delay += Random.Shared.Next(-delay / 5, delay / 5 + 1);
        await Task.Delay(Math.Max(delay, 100), stoppingToken).ConfigureAwait(false);
    }

    private void MarkStreamDown(
        WatchTarget target,
        DebounceChannel[] channels,
        WatchLoopState state,
        Exception? exception,
        int? statusCode)
    {
        state.PreviousAttemptFailed = true;
        if (!state.ReportedDown)
        {
            state.ReportedDown = true;
            foreach (DebounceChannel channel in channels)
            {
                channel.Signal.ReportStreamDown();
            }
        }

        if (state.WarnedDown)
        {
            // Déjà signalé : ne pas saturer les logs à chaque tentative de reconnexion.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception,
                    "Watch stream {Target} still unavailable (HTTP {StatusCode}): retrying after backoff",
                    target.Name,
                    statusCode);
            }

            return;
        }

        state.WarnedDown = true;
        logger.LogWarning(exception,
            "Watch stream {Target} unavailable (HTTP {StatusCode}): falling back to the legacy polling cadence until the stream is restored. " +
            "Check that the service account grants the \"watch\" verb on {Target}",
            target.Name,
            statusCode,
            target.Name);
    }

    private void MarkStreamUp(WatchTarget target, DebounceChannel[] channels, WatchLoopState state, bool logRecovery)
    {
        if (state.ReportedDown)
        {
            state.ReportedDown = false;
            foreach (DebounceChannel channel in channels)
            {
                channel.Signal.ReportStreamUp();
            }
        }

        if (state.WarnedDown)
        {
            if (logRecovery && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Watch stream {Target} restored: event-driven synchronization resumed", target.Name);
            }

            state.WarnedDown = false;
        }
    }

    private static void SignalDirty(DebounceChannel[] channels, KubernetesWatchOptions options, CancellationToken ct)
    {
        foreach (DebounceChannel channel in channels)
        {
            if (channel.TryArm())
            {
                _ = DebounceThenPulseAsync(channel, options.DebounceMilliseconds, ct);
            }
        }
    }

    private static void PulseAll(DebounceChannel[] channels)
    {
        foreach (DebounceChannel channel in channels)
        {
            channel.Signal.Pulse();
        }
    }

    private static async Task DebounceThenPulseAsync(DebounceChannel channel, int debounceMilliseconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(debounceMilliseconds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arrêt en cours : on pulse quand même, un réveil superflu est sans effet.
        }

        // Désarmement AVANT le pulse : un événement arrivant pendant le pulse ré-arme.
        channel.Disarm();
        channel.Signal.Pulse();
    }
}
