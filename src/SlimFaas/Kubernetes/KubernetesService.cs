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
public partial class KubernetesService : IKubernetesService
{
    // ── Annotation keys ───────────────────────────────────────────────────────
    private const string ReplicasMin = "SlimFaas/ReplicasMin";
    private const string Schedule = "SlimFaas/Schedule";
    private const string Configuration = "SlimFaas/Configuration";
    private const string Function = "SlimFaas/Function";
    private const string ReplicasAtStart = "SlimFaas/ReplicasAtStart";
    private const string DependsOn = "SlimFaas/DependsOn";
    private const string SubscribeEvents = "SlimFaas/SubscribeEvents";
    private const string DefaultVisibility = "SlimFaas/DefaultVisibility";
    private const string PathsStartWithVisibility = "SlimFaas/PathsStartWithVisibility";
    private const string Scale = "SlimFaas/Scale";
    private const string Job = "SlimFaas/Job";
    private const string JobImagesWhitelist = "SlimFaas/JobImagesWhitelist";
    private const string NumberParallelJob = "SlimFaas/NumberParallelJob";
    private const string JobSchedules = "SlimFaas/Schedules";

    private const string ReplicasStartAsSoonAsOneFunctionRetrieveARequest =
        "SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest";

    private const string TimeoutSecondBeforeSetReplicasMin = "SlimFaas/TimeoutSecondBeforeSetReplicasMin";
    private const string NumberParallelRequest = "SlimFaas/NumberParallelRequest";
    private const string NumberParallelRequestPerPod = "SlimFaas/NumberParallelRequestPerPod";
    private const string DefaultTrust = "SlimFaas/DefaultTrust";

    // ── Well-known resource / label names ─────────────────────────────────────
    private const string SlimfaasDeploymentName = "slimfaas";

    private const string SlimfaasJobName = "slimfaas-job-name";
    private const string SlimfaasJobElementId = "slimfaas-job-element-id";
    private const string SlimfaasInQueueTimestamp = "slimfaas-in-queue-timestamp";
    private const string SlimfaasJobStartTimestamp = "slimfaas-job-start-timestamp";

    public const string SlimfaasJobKey = "-slimfaas-job-";

    // ── Shared state ──────────────────────────────────────────────────────────
    private readonly k8s.Kubernetes _client;
    private readonly ILogger<KubernetesService> _logger;
    private bool _serviceListForbidden;

    public KubernetesService(ILogger<KubernetesService> logger, bool useKubeConfig)
    {
        _logger = logger;
        KubernetesClientConfiguration k8SConfig = !useKubeConfig
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        k8SConfig.SkipTlsVerify = true;
        _client = new k8s.Kubernetes(k8SConfig);
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

            _logger.LogWarning(ex,
                "Insufficient RBAC permissions to list Services in namespace {Namespace}. ServiceName will be null in PodInformation.",
                kubeNamespace);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error while listing Services in namespace {Namespace}. ServiceName will be null in PodInformation.",
                kubeNamespace);

            return null;
        }
    }
}
