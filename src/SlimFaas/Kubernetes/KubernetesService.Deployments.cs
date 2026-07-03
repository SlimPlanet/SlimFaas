using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace SlimFaas.Kubernetes;

public partial class KubernetesService
{
    public async Task<DeploymentsInformations> ListFunctionsAsync(string kubeNamespace,
        DeploymentsInformations previousDeployments)
    {
        try
        {
            k8s.Kubernetes client = _client;
            IList<DeploymentInformation>? deploymentInformationList = new List<DeploymentInformation>();

            Task<V1DeploymentList>? deploymentListTask = client.ListNamespacedDeploymentAsync(kubeNamespace);
            Task<V1PodList>? podListTask = client.ListNamespacedPodAsync(kubeNamespace);
            Task<V1StatefulSetList>? statefulSetListTask = client.ListNamespacedStatefulSetAsync(kubeNamespace);
            Task<V1ServiceList?> serviceListTask = TryListServicesAsync(kubeNamespace);

            await Task.WhenAll(deploymentListTask, podListTask, statefulSetListTask, serviceListTask);
            V1DeploymentList? deploymentList = await deploymentListTask;
            IEnumerable<PodInformation> podList = MapPodInformations(await podListTask, await serviceListTask, _logger);
            V1StatefulSetList? statefulSetList = await statefulSetListTask;

            SlimFaasDeploymentInformation? slimFaasDeploymentInformation = statefulSetList.Items
                .Where(deploymentListItem => deploymentListItem.Metadata.Name == SlimfaasDeploymentName).Select(
                    deploymentListItem =>
                        new SlimFaasDeploymentInformation(deploymentListItem.Spec.Replicas ?? 0,
                            podList.Where(p => p.DeploymentName == deploymentListItem.Metadata.Name).ToList()))
                .FirstOrDefault();

            IEnumerable<PodInformation> podInformations = podList.ToArray();
            await AddDeployments(kubeNamespace, deploymentList, podInformations, deploymentInformationList, _logger,
                client, previousDeployments.Functions);
            await AddStatefulSets(kubeNamespace, statefulSetList, podInformations, deploymentInformationList, _logger,
                client, previousDeployments.Functions);

            return new DeploymentsInformations(deploymentInformationList,
                slimFaasDeploymentInformation ?? new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
                podInformations);
        }
        catch (HttpOperationException e)
        {
            _logger.LogError(e, "Error while listing kubernetes functions");
            return previousDeployments;
        }
    }

    private static string? FindServiceNameForPod(V1Pod pod, V1ServiceList? services)
    {
        // Pas de droits / erreur => services == null => on renvoie juste null
        if (services == null || pod.Metadata?.Labels == null || services.Items == null)
        {
            return null;
        }

        foreach (var svc in services.Items)
        {
            var selector = svc.Spec?.Selector;
            if (selector == null || selector.Count == 0)
            {
                continue;
            }

            bool match = selector.All(kv =>
                pod.Metadata.Labels.TryGetValue(kv.Key, out var v) &&
                string.Equals(v, kv.Value, StringComparison.Ordinal));

            if (match)
            {
                return svc.Metadata?.Name;
            }
        }

        return null;
    }

    private static ResourcesConfiguration? ExtractResources(IList<V1Container>? containers)
    {
        if (containers is null || containers.Count == 0) return null;
        var c = containers[0];
        var req = c.Resources?.Requests;
        var lim = c.Resources?.Limits;
        return new ResourcesConfiguration(
            CpuRequest: req?.TryGetValue("cpu", out var cpuReq) == true ? cpuReq.ToString() : null,
            CpuLimit: lim?.TryGetValue("cpu", out var cpuLim) == true ? cpuLim.ToString() : null,
            MemoryRequest: req?.TryGetValue("memory", out var memReq) == true ? memReq.ToString() : null,
            MemoryLimit: lim?.TryGetValue("memory", out var memLim) == true ? memLim.ToString() : null
        );
    }

    private static async Task AddDeployments(string kubeNamespace, V1DeploymentList deploymentList,
        IEnumerable<PodInformation> podList,
        IList<DeploymentInformation> deploymentInformationList, ILogger<KubernetesService> logger,
        k8s.Kubernetes client, IList<DeploymentInformation> previousDeploymentInformationList)
    {
        foreach (V1Deployment? deploymentListItem in deploymentList.Items)
        {
            try
            {
                IDictionary<string, string>? annotations = deploymentListItem.Spec.Template?.Metadata?.Annotations;
                if (annotations == null || !annotations.ContainsKey(Function) ||
                    annotations[Function].ToLower() != "true")
                {
                    continue;
                }

                string? name = deploymentListItem.Metadata.Name;
                List<PodInformation> pods = podList.Where(p => p.DeploymentName.StartsWith(name)).ToList();
                DeploymentInformation? previousDeployment =
                    previousDeploymentInformationList.FirstOrDefault(d => d.Deployment == name);
                bool endpointReady = GetEndpointReady(logger, kubeNamespace, client, previousDeployment, name, pods);
                StringBuilder resourceVersionBuilder = new($"{deploymentListItem.Metadata.ResourceVersion}-{endpointReady}");
                foreach (PodInformation pod in pods)
                {
                    resourceVersionBuilder.Append($"-{pod.ResourceVersion}");
                }

                var resourceVersion = resourceVersionBuilder.ToString();
                if (previousDeployment != null && previousDeployment.ResourceVersion == resourceVersion)
                {
                    deploymentInformationList.Add(previousDeployment);
                }
                else
                {
                    SlimFaasConfiguration configuration = GetConfiguration(annotations, name, logger);
                    ScaleConfig? scaleConfig = GetScaleConfig(annotations, name, logger);
                    ScheduleConfig? scheduleConfig = GetScheduleConfig(annotations, name, logger);
                    FunctionVisibility funcVisibility =
                        annotations.TryGetValue(DefaultVisibility, out string? visibility)
                            ? Enum.Parse<FunctionVisibility>(visibility)
                            : FunctionVisibility.Public;
                    ResourcesConfiguration? resources = ExtractResources(deploymentListItem.Spec.Template?.Spec?.Containers);

                    DeploymentInformation deploymentInformation = new(
                        name,
                        kubeNamespace,
                        pods,
                        configuration,
                        deploymentListItem.Spec.Replicas ?? 0,
                        annotations.TryGetValue(ReplicasAtStart, out string? annotationReplicasAtStart)
                            ? int.Parse(annotationReplicasAtStart)
                            : 1, annotations.TryGetValue(ReplicasMin, out string? annotationReplicaMin)
                            ? int.Parse(annotationReplicaMin)
                            : 0, annotations.TryGetValue(TimeoutSecondBeforeSetReplicasMin,
                            out string? annotationTimeoutSecondBeforeSetReplicasMin)
                            ? int.Parse(annotationTimeoutSecondBeforeSetReplicasMin)
                            : 300, annotations.TryGetValue(NumberParallelRequest,
                            out string? annotationNumberParallelRequest)
                            ? int.Parse(annotationNumberParallelRequest)
                            : 10, annotations.ContainsKey(
                                      ReplicasStartAsSoonAsOneFunctionRetrieveARequest) &&
                                  annotations[ReplicasStartAsSoonAsOneFunctionRetrieveARequest].ToLower() == "true",
                        PodType.Deployment,
                        annotations.TryGetValue(DependsOn, out string? value)
                            ? value.Split(',').ToList()
                            : new List<string>(),
                        scheduleConfig,
                        GetSubscribeEvents(annotations, logger, funcVisibility),
                        funcVisibility,
                        GetPathsStartWithVisibility(annotations, name, logger),
                        resourceVersion,
                        endpointReady,
                        annotations.TryGetValue(DefaultTrust, out string? trust)
                            ? Enum.Parse<FunctionTrust>(trust)
                            : FunctionTrust.Trusted,
                        scaleConfig,
                        annotations.TryGetValue(NumberParallelRequestPerPod,
                            out string? annotationNumberParallelRequestPerPod)
                            ? int.Parse(annotationNumberParallelRequestPerPod)
                            : 10,
                        resources
                    );
                    deploymentInformationList.Add(deploymentInformation);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while adding deployment {Deployment}", deploymentListItem.Metadata.Name);
            }
        }
    }

    private static IList<PathVisibility> GetPathsStartWithVisibility(
        IDictionary<string, string> annotations,
        string name,
        ILogger<KubernetesService> logger)
    {
        // 1) Check if the annotation exists and is not empty
        if (!annotations.TryGetValue(PathsStartWithVisibility, out string? rawValue) ||
            string.IsNullOrWhiteSpace(rawValue))
        {
            return Array.Empty<PathVisibility>();
        }

        // 2) Split by commas to get individual tokens
        List<PathVisibility> paths = rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(token =>
            {
                // 3) Look for a possible prefix like "Public:" or "Private:"
                string[] parts = token.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);

                // Default visibility is Public
                FunctionVisibility visibility = FunctionVisibility.Public;
                string path;

                if (parts.Length == 2)
                {
                    string prefix = parts[0].Trim();
                    path = parts[1].Trim();

                    if (prefix.Equals("Private", StringComparison.OrdinalIgnoreCase))
                    {
                        visibility = FunctionVisibility.Private;
                    }
                    else if (prefix.Equals("Public", StringComparison.OrdinalIgnoreCase))
                    {
                        visibility = FunctionVisibility.Public;
                    }
                    else if (!prefix.Equals("Public", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning(
                            "Unknown prefix '{Prefix}' for path '{Path}'. The default (Public) visibility will be used.",
                            prefix,
                            path
                        );
                    }
                }
                else
                {
                    // No prefix => use the entire token as the path, defaulting to Public
                    path = token.Trim();
                }

                return new PathVisibility(path, visibility);
            })
            .ToList();

        return paths;
    }

    private static bool GetEndpointReady(ILogger<KubernetesService> logger, string kubeNamespace, k8s.Kubernetes client,
        DeploymentInformation? previousDeployment, string name, List<PodInformation> pods) =>
        pods.Count != 0 && pods.Any(p => p.Ports?.Count > 0);

    private static IList<SubscribeEvent> GetSubscribeEvents(
        IDictionary<string, string> annotations,
        ILogger<KubernetesService> logger, FunctionVisibility defaultVisibility = FunctionVisibility.Public)
    {
        // 1) Vérifier si l’annotation existe et n’est pas vide
        if (!annotations.TryGetValue(SubscribeEvents, out string? rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return Array.Empty<SubscribeEvent>();
        }

        // 2) Extraire les événements
        List<SubscribeEvent> events = rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(token =>
            {
                // On recherche un éventuel préfixe de type "Public:" ou "Private:"
                string[] parts = token.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);

                // On considère par défaut la visibilité comme Public
                FunctionVisibility visibility = defaultVisibility;
                string eventName;

                if (parts.Length == 2)
                {
                    string prefix = parts[0].Trim();
                    eventName = parts[1].Trim();

                    if (prefix.Equals("Private", StringComparison.OrdinalIgnoreCase))
                    {
                        visibility = FunctionVisibility.Private;
                    }
                    else if (prefix.Equals("Public", StringComparison.OrdinalIgnoreCase))
                    {
                        visibility = FunctionVisibility.Public;
                    }
                    else if (!prefix.Equals("Public", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning(
                            "Unknown prefix '{Prefix}' for event '{EventName}'. The default (Public) visibility will be used.",
                            prefix,
                            eventName
                        );
                    }
                }
                else
                {
                    // Pas de préfixe => eventName = token complet, visibilité par défaut Public
                    eventName = token.Trim();
                }

                return new SubscribeEvent(eventName, visibility);
            })
            .ToList();

        return events;
    }

    private static ScheduleConfig? GetScheduleConfig(IDictionary<string, string> annotations, string name,
        ILogger<KubernetesService> logger)
    {
        try
        {
            if (annotations.TryGetValue(Schedule, out string? annotation) && !string.IsNullOrEmpty(annotation.Trim()))
            {
                annotation = JsonMinifier.MinifyJson(annotation);
                if (!string.IsNullOrEmpty(annotation))
                {
                    return JsonSerializer.Deserialize(annotation,
                        ScheduleConfigSerializerContext.Default.ScheduleConfig);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "name: {Name}\\n annotations[Schedule]: {Annotation}", name, annotations[Schedule]);
        }

        return new ScheduleConfig();
    }

    private static SlimFaasConfiguration GetConfiguration(IDictionary<string, string> annotations, string name,
        ILogger<KubernetesService> logger)
    {
        try
        {
            if (annotations.TryGetValue(Configuration, out string? annotation) &&
                !string.IsNullOrEmpty(annotation.Trim()))
            {
                annotation = JsonMinifier.MinifyJson(annotation);
                if (!string.IsNullOrEmpty(annotation))
                {
                    return JsonSerializer.Deserialize(annotation,
                               SlimFaasConfigurationSerializerContext.Default.SlimFaasConfiguration) ??
                           new SlimFaasConfiguration();
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "name: {Name}\\n annotations[Configuration]: {Configuration}", name,
                annotations[Configuration]);
        }

        return new SlimFaasConfiguration();
    }

    private static async Task AddStatefulSets(string kubeNamespace, V1StatefulSetList deploymentList,
        IEnumerable<PodInformation> podList,
        IList<DeploymentInformation> deploymentInformationList, ILogger<KubernetesService> logger,
        k8s.Kubernetes client, IList<DeploymentInformation> previousDeploymentInformationList)
    {
        foreach (V1StatefulSet? deploymentListItem in deploymentList.Items)
        {
            try
            {
                IDictionary<string, string>? annotations = deploymentListItem.Spec.Template?.Metadata?.Annotations;
                if (annotations == null || !annotations.ContainsKey(Function) ||
                    annotations[Function].ToLower() != "true")
                {
                    continue;
                }

                string? name = deploymentListItem.Metadata.Name;
                List<PodInformation> pods = podList.Where(p => p.DeploymentName.StartsWith(name)).ToList();
                DeploymentInformation? previousDeployment =
                    previousDeploymentInformationList.FirstOrDefault(d => d.Deployment == name);
                bool endpointReady = GetEndpointReady(logger, kubeNamespace, client, previousDeployment, name, pods);
                string resourceVersion = $"{deploymentListItem.Metadata.ResourceVersion}-{endpointReady}";
                if (previousDeployment != null && previousDeployment.ResourceVersion == resourceVersion)
                {
                    deploymentInformationList.Add(previousDeployment);
                }
                else
                {
                    ScheduleConfig? scheduleConfig = GetScheduleConfig(annotations, name, logger);
                    SlimFaasConfiguration configuration = GetConfiguration(annotations, name, logger);
                    FunctionVisibility funcVisibility =
                        annotations.TryGetValue(DefaultVisibility, out string? visibility)
                            ? Enum.Parse<FunctionVisibility>(visibility)
                            : FunctionVisibility.Public;
                    ScaleConfig? scaleConfig = GetScaleConfig(annotations, name, logger);
                    ResourcesConfiguration? resources = ExtractResources(deploymentListItem.Spec.Template?.Spec?.Containers);
                    DeploymentInformation deploymentInformation = new(
                        name,
                        kubeNamespace,
                        pods,
                        configuration,
                        deploymentListItem.Spec.Replicas ?? 0,
                        annotations.TryGetValue(ReplicasAtStart, out string? annotationReplicasAtStart)
                            ? int.Parse(annotationReplicasAtStart)
                            : 1, annotations.TryGetValue(ReplicasMin, out string? annotationReplicasMin)
                            ? int.Parse(annotationReplicasMin)
                            : 0, annotations.TryGetValue(TimeoutSecondBeforeSetReplicasMin,
                            out string? annotationTimeoutSecondBeforeSetReplicasMin)
                            ? int.Parse(annotationTimeoutSecondBeforeSetReplicasMin)
                            : 300, annotations.TryGetValue(NumberParallelRequest,
                            out string? annotationNumberParallelRequest)
                            ? int.Parse(annotationNumberParallelRequest)
                            : 10, annotations.ContainsKey(
                                      ReplicasStartAsSoonAsOneFunctionRetrieveARequest) &&
                                  annotations[ReplicasStartAsSoonAsOneFunctionRetrieveARequest].ToLower() == "true",
                        PodType.StatefulSet,
                        annotations.TryGetValue(DependsOn, out string? value)
                            ? value.Split(',').ToList()
                            : new List<string>(),
                        scheduleConfig,
                        GetSubscribeEvents(annotations, logger, funcVisibility),
                        funcVisibility,
                        GetPathsStartWithVisibility(annotations, name, logger),
                        resourceVersion,
                        endpointReady,
                        annotations.TryGetValue(DefaultTrust, out string? trust)
                            ? Enum.Parse<FunctionTrust>(trust)
                            : FunctionTrust.Trusted,
                        scaleConfig,
                        annotations.TryGetValue(NumberParallelRequestPerPod,
                            out string? annotationNumberParallelRequestPerPod)
                            ? int.Parse(annotationNumberParallelRequestPerPod)
                            : 10,
                        resources);

                    deploymentInformationList.Add(deploymentInformation);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while adding statefulset {Deployment}", deploymentListItem.Metadata.Name);
            }
        }
    }

    private static IEnumerable<PodInformation> MapPodInformations(
        V1PodList v1PodList,
        V1ServiceList? serviceList,
        ILogger<KubernetesService> logger)
    {
        List<PodInformation> result = new();

        // v1PodList.Items peut être null
        if (v1PodList?.Items == null)
        {
            return result;
        }

        foreach (V1Pod? item in v1PodList.Items)
        {
            if (item is null)
            {
                continue;
            }

            try
            {
                string podIp = item.Status?.PodIP ?? string.Empty;

                var metadata = item.Metadata;
                var ownerRefs = metadata?.OwnerReferences;

                if (ownerRefs == null || ownerRefs.Count == 0)
                {
                    // c'est un job (ou un pod orphelin) -> on ignore
                    continue;
                }

                // ContainerStatuses peut être null
                V1ContainerStatus? containerStatus = item.Status?.ContainerStatuses?.FirstOrDefault();
                bool started = containerStatus?.Started ?? false;

                // Conditions peut être null
                var conditions = item.Status?.Conditions;
                bool containerReady =
                    conditions?
                        .FirstOrDefault(c => c.Type == "ContainersReady")?.Status == "True";

                bool podReady =
                    conditions?
                        .FirstOrDefault(c => c.Type == "Ready")?.Status == "True";

                string podName = metadata?.Name ?? string.Empty;
                string deploymentName = ownerRefs[0].Name ?? string.Empty;
                string resourceVersion = metadata?.ResourceVersion ?? string.Empty;

                // Spec ou Containers peuvent être null
                var containers = item.Spec?.Containers;
                List<int> ports = containers != null
                    ? containers
                        .Where(c => c?.Ports != null)
                        .SelectMany(c => c!.Ports!)
                        .Where(p => p != null && p.ContainerPort > 0)
                        .Select(p => p!.ContainerPort)
                        .ToList()
                    : new List<int>();

                string? serviceName = FindServiceNameForPod(item, serviceList);

                // 👉 Récupération de l'erreur de scheduling (quota / etc.)
                var schedCondition = conditions?
                    .FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");

                string? startFailureReason = schedCondition?.Reason;
                string? startFailureMessage = schedCondition?.Message;

                // Crash applicatif / container
                string? appFailureReason = null;
                string? appFailureMessage = null;

                // On ne regarde les containers que si ce n'est pas déjà un problème de scheduling
                if (schedCondition is null &&
                    item.Status?.ContainerStatuses is { Count: > 0 } containerStatuses)
                {
                    foreach (var cs in containerStatuses)
                    {
                        var waiting = cs.State?.Waiting;
                        var terminated = cs.State?.Terminated;

                        if (waiting is { Reason: not null })
                        {
                            appFailureReason = waiting.Reason;
                            appFailureMessage = waiting.Message;
                            break;
                        }

                        if (terminated is { Reason: not null })
                        {
                            appFailureReason = terminated.Reason;
                            appFailureMessage = terminated.Message;
                            break;
                        }
                    }
                }

                PodInformation podInformation = new(
                    podName,
                    started,
                    started && containerReady && podReady,
                    podIp,
                    deploymentName,
                    ports,
                    resourceVersion,
                    serviceName)
                {
                    Annotations = metadata?.Annotations,
                    StartFailureReason = startFailureReason,
                    StartFailureMessage = startFailureMessage,
                    AppFailureReason = appFailureReason,
                    AppFailureMessage = appFailureMessage
                };

                result.Add(podInformation);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Error while mapping pod informations for pod {PodName}: {Error}",
                    item.Metadata?.Name ?? "<unknown>",
                    ex.Message);
            }
        }

        return result;
    }
}
