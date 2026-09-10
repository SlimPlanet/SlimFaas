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
        public KubernetesResourceSignal Signal { get; } = signal;
        public int Pending;
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

        WatchTarget[] targets =
        [
            new("pods", $"api/v1/namespaces/{ns}/pods", [signals.Functions, signals.Jobs]),
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

        logger.LogInformation(
            "KubernetesWatcherWorker starting: watching pods/deployments/statefulsets/jobs/cronjobs in namespace {Namespace}",
            ns);

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
        string? lastResourceVersion = null;
        int backoffMilliseconds = options.ReconnectInitialDelayMilliseconds;
        bool previousAttemptFailed = false;

        async Task BackoffAsync()
        {
            int delay = backoffMilliseconds;
            backoffMilliseconds = Math.Min(backoffMilliseconds * 2, options.ReconnectMaxDelayMilliseconds);
            // Jitter ±20 % pour éviter les reconnexions synchronisées entre replicas.
            delay += Random.Shared.Next(-delay / 5, delay / 5 + 1);
            await Task.Delay(Math.Max(delay, 100), stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string url = string.Concat(
                    client.BaseUri,
                    target.PathTemplate,
                    $"?watch=true&allowWatchBookmarks=true&timeoutSeconds={options.WatchTimeoutSeconds}");
                if (!string.IsNullOrEmpty(lastResourceVersion))
                {
                    url += $"&resourceVersion={lastResourceVersion}";
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
                    lastResourceVersion = null;
                    PulseAll(channels);
                    await BackoffAsync().ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // 403/404/5xx : dégradation en resync périodique uniquement.
                    logger.LogWarning(
                        "Watch stream {Target} failed with HTTP {StatusCode}: retrying after backoff (resync interval still applies)",
                        target.Name,
                        (int)response.StatusCode);
                    previousAttemptFailed = true;
                    await BackoffAsync().ConfigureAwait(false);
                    continue;
                }

                backoffMilliseconds = options.ReconnectInitialDelayMilliseconds;
                if (previousAttemptFailed)
                {
                    // Des événements ont pu être manqués pendant la coupure.
                    previousAttemptFailed = false;
                    PulseAll(channels);
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(stoppingToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                bool sawError = false;
                while (!stoppingToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break; // rotation serveur (timeoutSeconds) : reconnexion avec la RV courante
                    }

                    WatchEventInfo eventInfo = WatchEventLineParser.Parse(line);
                    switch (eventInfo.Kind)
                    {
                        case WatchEventKind.Change:
                            if (eventInfo.ResourceVersion is not null)
                            {
                                lastResourceVersion = eventInfo.ResourceVersion;
                            }
                            SignalDirty(channels, options, stoppingToken);
                            break;
                        case WatchEventKind.Bookmark:
                            if (eventInfo.ResourceVersion is not null)
                            {
                                lastResourceVersion = eventInfo.ResourceVersion;
                            }
                            break;
                        case WatchEventKind.Error:
                            if (eventInfo.ErrorCode == 410)
                            {
                                lastResourceVersion = null;
                            }
                            logger.LogWarning(
                                "Watch stream {Target} received an ERROR event (code {Code}): reconnecting",
                                target.Name,
                                eventInfo.ErrorCode);
                            PulseAll(channels);
                            sawError = true;
                            break;
                        default:
                            logger.LogDebug("Watch stream {Target}: ignoring unknown line", target.Name);
                            break;
                    }

                    if (sawError)
                    {
                        break;
                    }
                }

                if (sawError)
                {
                    await BackoffAsync().ConfigureAwait(false);
                }
                // Fin de flux normale : reconnexion immédiate avec continuité de resourceVersion.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Inclut TaskCanceledException issue du timeout HttpClient (~100 s).
                logger.LogWarning(ex,
                    "Watch stream {Target} interrupted: reconnecting after backoff",
                    target.Name);
                previousAttemptFailed = true;
                try
                {
                    await BackoffAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void SignalDirty(DebounceChannel[] channels, KubernetesWatchOptions options, CancellationToken ct)
    {
        foreach (DebounceChannel channel in channels)
        {
            if (Interlocked.Exchange(ref channel.Pending, 1) == 0)
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
        }

        // Remise à zéro AVANT le pulse : un événement arrivant pendant le pulse ré-arme.
        Interlocked.Exchange(ref channel.Pending, 0);
        channel.Signal.Pulse();
    }
}
