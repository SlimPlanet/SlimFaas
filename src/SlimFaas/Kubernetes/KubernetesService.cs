using System.Diagnostics.CodeAnalysis;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace SlimFaas.Kubernetes;

/// <summary>
/// Kubernetes integration entry point used by SlimFaas.
///
/// The implementation is split across several partial files, each covering
/// a single concern to keep this class maintainable:
///
///   * <see cref="KubernetesService"/> (this file) — constructor, shared
///     state and low-level Kubernetes API helpers.
///   * <c>KubernetesService.Scaling.cs</c> — scaling logic and
///     <c>ScaleConfig</c> annotation parsing.
///   * <c>KubernetesService.Deployments.cs</c> — listing deployments,
///     stateful sets, pods and services, plus annotation extraction helpers.
///   * <c>KubernetesService.Jobs.cs</c> — job creation, listing and deletion.
///   * <c>KubernetesService.JobsConfiguration.cs</c> — extraction of the
///     SlimFaas job configuration from suspended <c>CronJob</c> resources.
///
/// Data types (records, enums, serialization contexts) live under the
/// <c>Models</c> sub-folder.
/// </summary>
[ExcludeFromCodeCoverage]
public partial class KubernetesService : IKubernetesService, IDisposable
{
    // ── Annotation keys ───────────────────────────────────────────────────────
    private const string Schedule = "SlimFaas/Schedule";
    private const string Configuration = "SlimFaas/Configuration";
    private const string Function = "SlimFaas/Function";
    private const string DependsOn = JobAnnotationNames.DependsOn;
    private const string SubscribeEvents = "SlimFaas/SubscribeEvents";
    private const string DefaultVisibility = JobAnnotationNames.DefaultVisibility;
    private const string PathsStartWithVisibility = "SlimFaas/PathsStartWithVisibility";
    private const string Scale = "SlimFaas/Scale";
    private const string Job = JobAnnotationNames.Job;
    private const string JobImagesWhitelist = JobAnnotationNames.JobImagesWhitelist;
    private const string NumberParallelJob = JobAnnotationNames.NumberParallelJob;
    private const string JobSchedules = JobAnnotationNames.Schedules;



    // ── Well-known resource / label names ─────────────────────────────────────
    private const string SlimfaasDeploymentName = "slimfaas";

    // Interne : réutilisé par KubernetesWatcherWorker pour scinder le watch des pods
    // (pods de jobs / pods de fonctions) avec le même sélecteur que ListJobsAsync.
    internal const string SlimfaasJobName = "slimfaas-job-name";
    private const string SlimfaasJobElementId = "slimfaas-job-element-id";
    private const string SlimfaasInQueueTimestamp = "slimfaas-in-queue-timestamp";
    private const string SlimfaasJobStartTimestamp = "slimfaas-job-start-timestamp";

    public const string SlimfaasJobKey = "-slimfaas-job-";

    // ── Shared state ──────────────────────────────────────────────────────────
    private readonly k8s.Kubernetes _client;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Dispose();
        }
    }
    internal k8s.Kubernetes LogClient => _client;
    private readonly ILogger<KubernetesService> _logger;
    private bool _serviceListForbidden;

    // Authenticated client shared with the watch worker (same config, same auth,
    // same connection pool) — see Watch/KubernetesWatcherWorker.
    internal k8s.Kubernetes Client => _client;

    public KubernetesService(ILogger<KubernetesService> logger, bool useKubeConfig, bool skipTlsVerify = false)
    {
        _logger = logger;
        // In-cluster: the CA projected into the pod is loaded and verified.
        // Kubeconfig: the file's own CA (or insecure-skip-tls-verify) applies.
        KubernetesClientConfiguration k8SConfig = !useKubeConfig
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        ApplyTlsVerification(k8SConfig, skipTlsVerify, logger);
        _client = new k8s.Kubernetes(k8SConfig);
    }

    /// <summary>
    /// Disables API-server certificate verification only when explicitly requested
    /// (<c>SlimFaas:KubernetesSkipTlsVerify</c>), and says so loudly at startup.
    /// </summary>
    internal static void ApplyTlsVerification(KubernetesClientConfiguration config, bool skipTlsVerify, ILogger logger)
    {
        if (!skipTlsVerify)
        {
            return;
        }

        config.SkipTlsVerify = true;
        logger.LogKubernetesApiServerTlsVerificationDisabled();
    }

    /// <summary>
    /// Attempts to list Kubernetes Services in the given namespace, remembering
    /// a <c>403 Forbidden</c> response so subsequent calls short-circuit and
    /// avoid noisy warnings.
    /// </summary>
    private async Task<V1ServiceList?> TryListServicesAsync(string kubeNamespace)
    {
        // Si on sait déjà qu’on n’a pas les droits, on ne refait pas l’appel
        if (_serviceListForbidden)
        {
            return null;
        }

        try
        {
            return await _client.ListNamespacedServiceAsync(kubeNamespace);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Forbidden)
        {
            _serviceListForbidden = true;

            _logger.LogInsufficientRBACPermissionsToListServices(ex, kubeNamespace);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWhileListingServicesInNamespace(ex, kubeNamespace);

            return null;
        }
    }
}
