using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.Autorest;
using k8s.Models;
using MemoryPack;

namespace SlimFaas.Kubernetes;

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    ImagePullBackOff
}
public record Job(string Name, JobStatus Status, IList<string> Ips);

public class ScheduleConfig
{
    public string TimeZoneID  { get; set; } = "GB";
    public DefaultSchedule? Default { get; set; } = new();
}

public record DefaultSchedule
{
    public List<string> WakeUp { get; init; } = new();
    public List<ScaleDownTimeout> ScaleDownTimeout { get; init; } = new();
}

public record ScaleDownTimeout
{
    public string Time { get; init; } = "00:00";
    public int Value { get; init; }
}

[JsonSerializable(typeof(ScheduleConfig))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ScheduleConfigSerializerContext : JsonSerializerContext;

public record SlimFaasConfiguration {

    public SlimFaasDefaultConfiguration DefaultSync { get; init; } = new();

    public SlimFaasDefaultConfiguration DefaultAsync { get; init; } = new();
    public SlimFaasDefaultConfiguration DefaultPublish { get; init; } = new();

}

public record SlimFaasDefaultConfiguration {

    public int HttpTimeout { get; init; } = 120;
    public List<int> TimeoutRetries { get; init; } = [2, 4, 8];
    public List<int> HttpStatusRetries { get; init; } = [500, 502, 503];
}

[JsonSerializable(typeof(SlimFaasConfiguration))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SlimFaasConfigurationSerializerContext : JsonSerializerContext;

public enum FunctionVisibility
{
    Public,
    Private
}

public enum FunctionTrust
{
    Trusted,
    Untrusted
}

public enum PodType
{
    Deployment,
    StatefulSet
}

public record SubscribeEvent(string Name, FunctionVisibility Visibility);
public record PathVisibility(string Path, FunctionVisibility Visibility);


public record ReplicaRequest(string Deployment, string Namespace, int Replicas, PodType PodType);

public record SlimFaasDeploymentInformation(int Replicas, IList<PodInformation> Pods);

public record DeploymentsInformations(IList<DeploymentInformation> Functions,
    SlimFaasDeploymentInformation SlimFaas, IEnumerable<PodInformation> Pods);

[JsonSerializable(typeof(DeploymentsInformations))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class DeploymentsInformationsSerializerContext : JsonSerializerContext;

public record DeploymentInformation(string Deployment,
    string Namespace,
    IList<PodInformation> Pods,
    SlimFaasConfiguration Configuration,
    int Replicas,
    int ReplicasAtStart = 1,
    int ReplicasMin = 0,
    int TimeoutSecondBeforeSetReplicasMin = 300,
    int NumberParallelRequest = 10,
    bool ReplicasStartAsSoonAsOneFunctionRetrieveARequest = false,
    PodType PodType = PodType.Deployment,
    IList<string>? DependsOn = null,
    ScheduleConfig? Schedule = null,
    IList<SubscribeEvent>? SubscribeEvents = null,
    FunctionVisibility Visibility = FunctionVisibility.Public,
    IList<PathVisibility>? PathsStartWithVisibility = null,
    string ResourceVersion = "",
    bool EndpointReady = false,
    FunctionTrust Trust = FunctionTrust.Trusted
    );

public record PodInformation(string Name, bool? Started, bool? Ready, string Ip, string DeploymentName, IList<int>? Ports = null);

[MemoryPackable]
public partial record CreateJob(

    List<string> Args,
    string Image = "",
    int BackoffLimit = 1,
    int TtlSecondsAfterFinished= 60,
    string RestartPolicy = "Never",
    CreateJobResources? Resources = null,
    IList<EnvVarInput>? Environments = null,
    string ConfigurationName = "Default");


[MemoryPackable]
public partial record SlimfaasJobConfiguration(Dictionary<string, SlimfaasJob> Configurations);


[MemoryPackable]
public partial record SlimfaasJob(
    string Image,
    List<string> ImagesWhitelist,
    CreateJobResources? Resources = null,
    List<string>? DependsOn = null,
    IList<EnvVarInput>? Environments = null,
    int BackoffLimit = 1,
    string Visibility = nameof(FunctionVisibility.Private),
    int NumberParallelJob = 1,
    int TtlSecondsAfterFinished= 60,
    string RestartPolicy = "Never");

[MemoryPackable]
public partial record EnvVarInput(
    string Name,
    string Value,
    SecretRef? SecretRef=null,
    ConfigMapRef? ConfigMapRef=null,
    FieldRef? FieldRef=null,
    ResourceFieldRef? ResourceFieldRef=null)
{
    public string Name { get; set; } = Name;

    public string Value { get; set; } = Value;

    public SecretRef? SecretRef { get; set; } = SecretRef;

    public ConfigMapRef? ConfigMapRef { get; set; } = ConfigMapRef;

    public FieldRef? FieldRef { get; set; } = FieldRef;

    public ResourceFieldRef? ResourceFieldRef { get; set; } = ResourceFieldRef;
}

[MemoryPackable]
public partial record SecretRef(string Name, string Key)
{
    public string Name { get; set; } = Name;
    public string Key { get; set; } = Key;
}

[MemoryPackable]
public partial record ConfigMapRef(string Name, string Key)
{
    public string Name { get; set; } = Name;
    public string Key { get; set; } = Key;
}

[MemoryPackable]
public partial record FieldRef(string FieldPath)
{
    public string FieldPath { get; set; } = FieldPath;
}

[MemoryPackable]
public partial record ResourceFieldRef(string ContainerName, string Resource, string  Divisor)
{
    public string ContainerName { get; set; } = ContainerName;
    public string Resource { get; set; } = Resource;
    public string  Divisor { get; set; } = Divisor;
}



[MemoryPackable]
public partial record CreateJobResources(Dictionary<string,string> Requests, Dictionary<string,string> Limits);


[JsonSerializable(typeof(SlimfaasJobConfiguration))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SlimfaasJobConfigurationSerializerContext : JsonSerializerContext;

[JsonSerializable(typeof(CreateJob))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class CreateJobSerializerContext : JsonSerializerContext;

[ExcludeFromCodeCoverage]
public class KubernetesService : IKubernetesService
{
    private const string ReplicasMin = "SlimFaas/ReplicasMin";
    private const string Schedule = "SlimFaas/Schedule";
    private const string Configuration = "SlimFaas/Configuration";
    private const string Function = "SlimFaas/Function";
    private const string ReplicasAtStart = "SlimFaas/ReplicasAtStart";
    private const string DependsOn = "SlimFaas/DependsOn";
    private const string SubscribeEvents = "SlimFaas/SubscribeEvents";
    private const string DefaultVisibility = "SlimFaas/DefaultVisibility";
    private const string PathsStartWithVisibility = "SlimFaas/PathsStartWithVisibility";

    private const string ReplicasStartAsSoonAsOneFunctionRetrieveARequest =
        "SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest";

    private const string TimeoutSecondBeforeSetReplicasMin = "SlimFaas/TimeoutSecondBeforeSetReplicasMin";
    private const string NumberParallelRequest = "SlimFaas/NumberParallelRequest";
    private const string DefaultTrust = "SlimFaas/DefaultTrust";

    private const string SlimfaasDeploymentName = "slimfaas";
    private readonly ILogger<KubernetesService> _logger;
    private readonly k8s.Kubernetes _client;

    public KubernetesService(ILogger<KubernetesService> logger, bool useKubeConfig)
    {
        _logger = logger;
        KubernetesClientConfiguration k8SConfig = !useKubeConfig
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        k8SConfig.SkipTlsVerify = true;
        _client = new k8s.Kubernetes(k8SConfig);
    }


    public async Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request)
    {
        try
        {
            var client = _client;
            string patchString = $"{{\"spec\": {{\"replicas\": {request.Replicas}}}}}";
            var httpContent = new StringContent(patchString, Encoding.UTF8, "application/merge-patch+json");
            // we need to get the base uri, as it's not set on the HttpClient
            switch (request.PodType)
            {
                case PodType.Deployment:
                    {
                        var url = string.Concat(client.BaseUri, $"apis/apps/v1/namespaces/{request.Namespace}/deployments/{request.Deployment}/scale" );
                        HttpRequestMessage httpRequest = new(HttpMethod.Patch,
                            new Uri(url));
                        httpRequest.Content = httpContent;
                        if ( client.Credentials != null )
                        {
                            await client.Credentials.ProcessHttpRequestAsync( httpRequest, CancellationToken.None );
                        }
                        var response = await client.HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                        if(response.StatusCode != HttpStatusCode.OK)
                        {
                            throw new HttpOperationException("Error while scaling deployment");
                        }
                        break;
                    }
                case PodType.StatefulSet:
                    {
                        var url = string.Concat(client.BaseUri, $"apis/apps/v1/namespaces/{request.Namespace}/statefulsets/{request.Deployment}/scale" );
                        HttpRequestMessage httpRequest = new(HttpMethod.Patch,
                            new Uri(url));
                        httpRequest.Content = httpContent;
                        if ( client.Credentials != null )
                        {
                            await client.Credentials.ProcessHttpRequestAsync( httpRequest, CancellationToken.None);
                        }
                        var response = await client.HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead );
                        if(response.StatusCode != HttpStatusCode.OK)
                        {
                            throw new HttpOperationException("Error while scaling deployment");
                        }
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException(request.PodType.ToString());
            }
        }
        catch (HttpOperationException e)
        {
            _logger.LogError(e, "Error while scaling kubernetes deployment {RequestDeployment}", request.Deployment);
            return request;
        }
        return request;
    }


    public async Task<DeploymentsInformations> ListFunctionsAsync(string kubeNamespace, DeploymentsInformations previousDeployments)
    {
        try
        {
            var client = _client;
            IList<DeploymentInformation>? deploymentInformationList = new List<DeploymentInformation>();

            Task<V1DeploymentList>? deploymentListTask = client.ListNamespacedDeploymentAsync(kubeNamespace);
            Task<V1PodList>? podListTask = client.ListNamespacedPodAsync(kubeNamespace);
            Task<V1StatefulSetList>? statefulSetListTask = client.ListNamespacedStatefulSetAsync(kubeNamespace);

            await Task.WhenAll(deploymentListTask, podListTask, statefulSetListTask);
            V1DeploymentList? deploymentList = deploymentListTask.Result;
            IEnumerable<PodInformation> podList = MapPodInformations(podListTask.Result);
            V1StatefulSetList? statefulSetList = statefulSetListTask.Result;

            SlimFaasDeploymentInformation? slimFaasDeploymentInformation = statefulSetList.Items
                .Where(deploymentListItem => deploymentListItem.Metadata.Name == SlimfaasDeploymentName).Select(
                    deploymentListItem =>
                        new SlimFaasDeploymentInformation(deploymentListItem.Spec.Replicas ?? 0,
                            podList.Where(p => p.Name.StartsWith(deploymentListItem.Metadata.Name)).ToList()))
                .FirstOrDefault();

            IEnumerable<PodInformation> podInformations = podList.ToArray();
            await AddDeployments(kubeNamespace, deploymentList, podInformations, deploymentInformationList, _logger, client, previousDeployments.Functions);
            await AddStatefulSets(kubeNamespace, statefulSetList, podInformations, deploymentInformationList, _logger, client, previousDeployments.Functions);

            return new DeploymentsInformations(deploymentInformationList,
                slimFaasDeploymentInformation ?? new SlimFaasDeploymentInformation(1, new List<PodInformation>()), podInformations);
        }
        catch (HttpOperationException e)
        {
            _logger.LogError(e, "Error while listing kubernetes functions");
            return previousDeployments;
        }
    }

    private static async Task AddDeployments(string kubeNamespace, V1DeploymentList deploymentList, IEnumerable<PodInformation> podList,
        IList<DeploymentInformation> deploymentInformationList, ILogger<KubernetesService> logger, k8s.Kubernetes client , IList<DeploymentInformation> previousDeploymentInformationList)
    {
        foreach (V1Deployment? deploymentListItem in deploymentList.Items)
        {
            try
            {
                var annotations = deploymentListItem.Spec.Template?.Metadata?.Annotations;
                if (annotations == null || !annotations.ContainsKey(Function) ||
                    annotations[Function].ToLower() != "true")
                {
                    continue;
                }

                var name = deploymentListItem.Metadata.Name;
                var pods = podList.Where(p => p.DeploymentName.StartsWith(name)).ToList();
                ScheduleConfig? scheduleConfig = GetScheduleConfig(annotations, name, logger);
                SlimFaasConfiguration configuration = GetConfiguration(annotations, name, logger);
                var previousDeployment = previousDeploymentInformationList.FirstOrDefault(d => d.Deployment == name);
                bool endpointReady = GetEndpointReady(logger, kubeNamespace, client, previousDeployment, name, pods);
                var resourceVersion = $"{deploymentListItem.Metadata.ResourceVersion}-{endpointReady}";
                if (previousDeployment != null && previousDeployment.ResourceVersion ==  resourceVersion)
                {
                    deploymentInformationList.Add(previousDeployment);
                }
                else
                {
                    var funcVisibility = annotations.TryGetValue(DefaultVisibility, out string? visibility)
                        ? Enum.Parse<FunctionVisibility>(visibility)
                        : FunctionVisibility.Public;
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
                        EndpointReady: endpointReady,
                        Trust: annotations.TryGetValue(DefaultTrust, out string? trust)
                            ? Enum.Parse<FunctionTrust>(trust)
                            : FunctionTrust.Trusted
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
        if (!annotations.TryGetValue(PathsStartWithVisibility, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return Array.Empty<PathVisibility>();
        }

        // 2) Split by commas to get individual tokens
        var paths = rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(token =>
            {
                // 3) Look for a possible prefix like "Public:" or "Private:"
                var parts = token.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);

                // Default visibility is Public
                FunctionVisibility visibility = FunctionVisibility.Public;
                string path;

                if (parts.Length == 2)
                {
                    var prefix = parts[0].Trim();
                    path = parts[1].Trim();

                    if (prefix.Equals("Private", StringComparison.OrdinalIgnoreCase))
                    {
                        visibility = FunctionVisibility.Private;
                    }
                    if (prefix.Equals("Public", StringComparison.OrdinalIgnoreCase))
                    {
                        visibility = FunctionVisibility.Public;
                    }
                    else if (!prefix.Equals("Public", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning(
                            "Unknown prefix '{prefix}' for path '{path}'. The default (Public) visibility will be used.",
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
        DeploymentInformation? previousDeployment, string name, List<PodInformation> pods)
    {
        return pods.Count != 0 && pods.Any(p => p.Ports?.Count > 0);
    }

private static IList<SubscribeEvent> GetSubscribeEvents(
    IDictionary<string, string> annotations,
    ILogger<KubernetesService> logger, FunctionVisibility defaultVisibility = FunctionVisibility.Public)
{

    // 1) Vérifier si l’annotation existe et n’est pas vide
    if (!annotations.TryGetValue(SubscribeEvents, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
    {
        return Array.Empty<SubscribeEvent>();
    }

    // 2) Extraire les événements
    var events = rawValue
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(token =>
        {
            // On recherche un éventuel préfixe de type "Public:" ou "Private:"
            var parts = token.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);

            // On considère par défaut la visibilité comme Public
            FunctionVisibility visibility = defaultVisibility;
            string eventName;

            if (parts.Length == 2)
            {
                var prefix = parts[0].Trim();
                eventName = parts[1].Trim();

                if (prefix.Equals("Private", StringComparison.OrdinalIgnoreCase))
                {
                    visibility = FunctionVisibility.Private;
                }
                if (prefix.Equals("Public", StringComparison.OrdinalIgnoreCase))
                {
                    visibility = FunctionVisibility.Public;
                }
                else if (!prefix.Equals("Public", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Unknown prefix '{prefix}' for event '{eventName}'. The default (Public) visibility will be used.",
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
    private static ScheduleConfig? GetScheduleConfig(IDictionary<string, string> annotations, string name, ILogger<KubernetesService> logger)
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
            logger.LogError( e, "name: {Name}\\n annotations[Schedule]: {Annotation}", name, annotations[Schedule]);
        }

        return new ScheduleConfig();
    }

    private static SlimFaasConfiguration GetConfiguration(IDictionary<string, string> annotations, string name, ILogger<KubernetesService> logger)
    {
        try
        {
            if (annotations.TryGetValue(Configuration, out string? annotation) && !string.IsNullOrEmpty(annotation.Trim()))
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
            logger.LogError( e, "name: {Name}\\n annotations[Configuration]: {Configuration}", name, annotations[Configuration]);
        }

        return new SlimFaasConfiguration();
    }

    private static async Task AddStatefulSets(string kubeNamespace, V1StatefulSetList deploymentList, IEnumerable<PodInformation> podList,
        IList<DeploymentInformation> deploymentInformationList, ILogger<KubernetesService> logger, k8s.Kubernetes client , IList<DeploymentInformation> previousDeploymentInformationList)
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

                var name = deploymentListItem.Metadata.Name;
                var pods = podList.Where(p => p.DeploymentName.StartsWith(name)).ToList();
                ScheduleConfig? scheduleConfig = GetScheduleConfig(annotations, name, logger);
                SlimFaasConfiguration configuration = GetConfiguration(annotations, name, logger);
                var previousDeployment = previousDeploymentInformationList.FirstOrDefault(d => d.Deployment == name);
                bool endpointReady = GetEndpointReady(logger, kubeNamespace, client, previousDeployment, name, pods);
                var resourceVersion = $"{deploymentListItem.Metadata.ResourceVersion}-{endpointReady}";
                if (previousDeployment != null && previousDeployment.ResourceVersion ==  resourceVersion)
                {
                    deploymentInformationList.Add(previousDeployment);
                }
                else
                {
                    var funcVisibility = annotations.TryGetValue(DefaultVisibility, out string? visibility)
                        ? Enum.Parse<FunctionVisibility>(visibility)
                        : FunctionVisibility.Public;
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
                        GetSubscribeEvents(annotations,logger, funcVisibility),
                        funcVisibility,
                        GetPathsStartWithVisibility(annotations, name, logger),
                        resourceVersion,
                        EndpointReady: endpointReady,
                        Trust: annotations.TryGetValue(DefaultTrust, out string? trust)
                            ? Enum.Parse<FunctionTrust>(trust)
                            : FunctionTrust.Trusted);

                    deploymentInformationList.Add(deploymentInformation);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while adding statefulset {Deployment}", deploymentListItem.Metadata.Name);
            }
        }
    }

    private static IEnumerable<PodInformation> MapPodInformations(V1PodList v1PodList)
    {
        var result = new List<PodInformation>();
        foreach (V1Pod? item in v1PodList.Items)
        {
            string? podIp = item.Status.PodIP;
            if (podIp == null || string.IsNullOrEmpty(podIp))
            {
                continue;
            }

            V1ContainerStatus? containerStatus = item.Status.ContainerStatuses.FirstOrDefault();
            bool started = containerStatus?.Started ?? false;
            bool containerReady = item.Status.Conditions.FirstOrDefault(c => c.Type == "ContainersReady")?.Status == "True";
            bool podReady = item.Status.Conditions.FirstOrDefault(c => c.Type == "Ready")?.Status == "True";
            string? podName = item.Metadata.Name;
            string deploymentName = item.Metadata.OwnerReferences[0].Name;

            var ports = item.Spec?.Containers
                .Where(c => c.Ports != null)
                .SelectMany(c => c.Ports)
                .Where(p => p.ContainerPort > 0)
                .Select(p => p.ContainerPort)
                .ToList() ?? new List<int>();

            PodInformation podInformation = new(podName, started, started && containerReady && podReady, podIp, deploymentName, Ports: ports);
            result.Add(podInformation);
        }
        return result;
    }


    public const string SlimfaasJobKey = "-slimfaas-job-";
    public async Task CreateJobAsync( string kubeNamespace, string name, CreateJob createJob)
    {
        var client = _client;

        var fullName = $"{name}{SlimfaasJobKey}{Guid.NewGuid()}";

        var requests = new Dictionary<string, ResourceQuantity>
        {
            { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("512Mi") }
        };
        CreateJobResources? createJobResources = createJob.Resources;
        if( createJobResources?.Requests != null )
        {
            requests = createJobResources.Requests.ToDictionary(r => r.Key, r => new ResourceQuantity(r.Value));
        }
        var limits = requests;
        if(createJobResources?.Limits != null)
        {
            limits = createJobResources.Limits.ToDictionary(r => r.Key, r => new ResourceQuantity(r.Value));
        }

        var envVars = createJob.Environments?.Select(e =>
        {
            if (e.SecretRef != null)
            {
                return new V1EnvVar(
                    name: e.Name,
                    valueFrom: new V1EnvVarSource
                    {
                        SecretKeyRef = new V1SecretKeySelector(
                            name: e.SecretRef.Name,
                            key: e.SecretRef.Key)
                    }
                );
            }
            else if (e.ConfigMapRef != null)
            {
                return new V1EnvVar(
                    name: e.Name,
                    valueFrom: new V1EnvVarSource
                    {
                        ConfigMapKeyRef = new V1ConfigMapKeySelector(
                            name: e.ConfigMapRef.Name,
                            key: e.ConfigMapRef.Key)
                    }
                );
            }
            else if (e.FieldRef != null)
            {
                return new V1EnvVar(
                    name: e.Name,
                    valueFrom: new V1EnvVarSource
                    {
                        FieldRef = new V1ObjectFieldSelector(
                            fieldPath: e.FieldRef.FieldPath)
                    }
                );
            }
            else if (e.ResourceFieldRef != null)
            {
                return new V1EnvVar(
                    name: e.Name,
                    valueFrom: new V1EnvVarSource
                    {
                        ResourceFieldRef = new V1ResourceFieldSelector(
                            containerName: e.ResourceFieldRef.ContainerName,
                            resource: e.ResourceFieldRef.Resource,
                            divisor: new ResourceQuantity(e.ResourceFieldRef.Divisor)
                        )
                    }
                );
            }
            else
            {
                return new V1EnvVar(name: e.Name, value: e.Value);
            }
        }).ToList();

        var job = new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta
            {
                Name =  fullName,
                NamespaceProperty = kubeNamespace
            },
            Spec = new V1JobSpec
            {
                TtlSecondsAfterFinished = createJob.TtlSecondsAfterFinished,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string> { { "job-name", fullName } }
                    },
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>
                        {
                            new()
                            {
                                Name = name,
                                Image = createJob.Image,
                                Args = createJob.Args,
                                Env = envVars,
                                Resources = new V1ResourceRequirements()
                                {
                                    Requests = requests,
                                    Limits = limits
                                }
                            }
                        },
                        RestartPolicy = createJob.RestartPolicy
                    }
                },
                BackoffLimit = createJob.BackoffLimit
            }
        };

        var jobResponse = await client.CreateNamespacedJobAsync(job, kubeNamespace);

        Console.WriteLine($"Job created with name: {jobResponse.Metadata.Name}");
    }


    public async Task<IList<Job>> ListJobsAsync(string kubeNamespace)
    {
        var jobStatus = new List<Job>();
        var client = _client;
        var jobList = await client.ListNamespacedJobAsync(kubeNamespace);
        foreach (V1Job v1Job in jobList)
        {
            var pods = await _client.ListNamespacedPodAsync(
                kubeNamespace,
                labelSelector: $"job-name={v1Job.Metadata.Name}"
            );

            IList<string> ips = pods.Items.Where(p => p.Status.PodIP != null).Select(p => p.Status.PodIP).ToList();

            JobStatus status = v1Job.Status.Active > 0 ? JobStatus.Running : JobStatus.Pending;
            if (v1Job.Status.Succeeded is > 0)
            {
                status = JobStatus.Succeeded;
            }
            else if (v1Job.Status.Failed is > 0)
            {
                status = JobStatus.Failed;
            }

            // Vérifier si un des pods est en PullBackOff ou ErrImagePull
            foreach (var pod in pods.Items)
            {
                if (pod.Status.ContainerStatuses == null)
                {
                    continue;
                }

                foreach (var containerStatus in pod.Status.ContainerStatuses)
                {
                    if (containerStatus.State.Waiting is { Reason: "ImagePullBackOff" or "ErrImagePull" })
                    {
                        status = JobStatus.ImagePullBackOff;
                    }
                }
            }

            jobStatus.Add(new Job(v1Job.Metadata.Name,
                status,
                Ips: ips
                ));
        }

        return jobStatus;
    }

    public async Task DeleteJobAsync(string kubeNamespace, string name)
    {
        var client = _client;
        await client.DeleteNamespacedJobAsync(name, kubeNamespace);
    }

}
