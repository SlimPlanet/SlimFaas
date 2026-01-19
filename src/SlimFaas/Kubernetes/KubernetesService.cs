using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.Autorest;
using k8s.Models;
using MemoryPack;
using SlimFaas.Jobs;

namespace SlimFaas.Kubernetes;

public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    ImagePullBackOff = 4,
}

public record Job(
    string Name,
    JobStatus Status,
    IList<string> Ips,
    IList<string> DependsOn,
    string ElementId,
    long InQueueTimestamp,
    long StartTimestamp);

public class ScheduleConfig
{
    public string TimeZoneID { get; set; } = "GB";
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

public record SlimFaasConfiguration
{
    public SlimFaasDefaultConfiguration DefaultSync { get; init; } = new();
    public SlimFaasDefaultConfiguration DefaultAsync { get; init; } = new();
    public SlimFaasDefaultConfiguration DefaultPublish { get; init; } = new();
}

public record SlimFaasDefaultConfiguration
{
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

public record DeploymentsInformations(
    IList<DeploymentInformation> Functions,
    SlimFaasDeploymentInformation SlimFaas,
    IEnumerable<PodInformation> Pods);

[JsonSerializable(typeof(DeploymentsInformations))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class DeploymentsInformationsSerializerContext : JsonSerializerContext;

public record DeploymentInformation(
    string Deployment,
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
    FunctionTrust Trust = FunctionTrust.Trusted,
    ScaleConfig? Scale = null,
    int NumberParallelRequestPerPod = 10
);

public record PodInformation(
    string Name,
    bool? Started,
    bool? Ready,
    string Ip,
    string DeploymentName,
    IList<int>? Ports = null,
    string ResourceVersion = "",
    string? ServiceName = null)
{
    public IDictionary<string, string>? Annotations { get; init; }
    public string? StartFailureReason { get; init; }
    public string? StartFailureMessage { get; init; }
    public string? AppFailureReason { get; init;}
    public string? AppFailureMessage { get; init; }
}

[MemoryPackable]
public partial record CreateJob(
    List<string> Args,
    string Image = "",
    int BackoffLimit = 1,
    int TtlSecondsAfterFinished = 60,
    string RestartPolicy = "Never",
    CreateJobResources? Resources = null,
    IList<EnvVarInput>? Environments = null,
    List<string>? DependsOn = null);

[MemoryPackable]
public partial record ScheduleCreateJob(
    string Schedule,
    List<string> Args,
    string Image = "",
    int BackoffLimit = 1,
    int TtlSecondsAfterFinished = 60,
    string RestartPolicy = "Never",
    CreateJobResources? Resources = null,
    IList<EnvVarInput>? Environments = null,
    List<string>? DependsOn = null);



[MemoryPackable]
public partial record SlimFaasJobConfiguration(Dictionary<string, SlimfaasJob> Configurations, Dictionary<string, IList<ScheduleCreateJob>>? Schedules=null);

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
    int TtlSecondsAfterFinished = 60,
    string RestartPolicy = "Never");



[MemoryPackable]
public partial record EnvVarInput(
    string Name,
    string Value,
    SecretRef? SecretRef = null,
    ConfigMapRef? ConfigMapRef = null,
    FieldRef? FieldRef = null,
    ResourceFieldRef? ResourceFieldRef = null)
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
public partial record ResourceFieldRef(string ContainerName, string Resource, string Divisor)
{
    public string ContainerName { get; set; } = ContainerName;
    public string Resource { get; set; } = Resource;
    public string Divisor { get; set; } = Divisor;
}

[MemoryPackable]
public partial record CreateJobResources(Dictionary<string, string> Requests, Dictionary<string, string> Limits);

[JsonSerializable(typeof(SlimFaasJobConfiguration))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SlimfaasJobConfigurationSerializerContext : JsonSerializerContext;

[JsonSerializable(typeof(CreateJob))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class CreateJobSerializerContext : JsonSerializerContext;

[JsonSerializable(typeof(ScheduleCreateJob))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ScheduleCreateJobSerializerContext : JsonSerializerContext;

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
    private const string Scale = "SlimFaas/Scale";
    private const string Job = "SlimFaas/Job";
    private const string JobImagesWhitelist = "SlimFaas/JobImagesWhitelist";

    private const string ReplicasStartAsSoonAsOneFunctionRetrieveARequest =
        "SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest";

    private const string TimeoutSecondBeforeSetReplicasMin = "SlimFaas/TimeoutSecondBeforeSetReplicasMin";
    private const string NumberParallelRequest = "SlimFaas/NumberParallelRequest";
    private const string NumberParallelRequestPerPod = "SlimFaas/NumberParallelRequestPerPod";
    private const string DefaultTrust = "SlimFaas/DefaultTrust";

    private const string SlimfaasDeploymentName = "slimfaas";

    private const string SlimfaasJobName = "slimfaas-job-name";
    private const string SlimfaasJobElementId = "slimfaas-job-element-id";
    private const string SlimfaasInQueueTimestamp = "slimfaas-in-queue-timestamp";
    private const string SlimfaasJobStartTimestamp = "slimfaas-job-start-timestamp";


    public const string SlimfaasJobKey = "-slimfaas-job-";
    private readonly k8s.Kubernetes _client;
    private readonly ILogger<KubernetesService> _logger;
    private IJobConfiguration _jobConfiguration;
    private bool _serviceListForbidden;

    public KubernetesService(ILogger<KubernetesService> logger, bool useKubeConfig, IJobConfiguration jobConfiguration)
    {
        _logger = logger;
        KubernetesClientConfiguration k8SConfig = !useKubeConfig
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        k8SConfig.SkipTlsVerify = true;
        _client = new k8s.Kubernetes(k8SConfig);
        _jobConfiguration = jobConfiguration;
    }

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


    private static ScaleConfig NormalizeScaleConfig(ScaleConfig? cfg)
    {
        // objet racine
        cfg ??= new ScaleConfig();

        // Behavior (et ses sous-objets)
        var behavior = cfg.Behavior ?? new ScaleBehavior();

        var scaleUp = behavior.ScaleUp ?? ScaleDirectionBehavior.DefaultScaleUp();
        var scaleDown = behavior.ScaleDown ?? ScaleDirectionBehavior.DefaultScaleDown();

        // Policies (si null -> defaults)
        var upPolicies = scaleUp.Policies is { Count: > 0 } ? scaleUp.Policies : ScaleDirectionBehavior.DefaultScaleUp().Policies;
        var downPolicies = scaleDown.Policies is { Count: > 0 } ? scaleDown.Policies : ScaleDirectionBehavior.DefaultScaleDown().Policies;

        // Triggers (si null -> liste vide)
        var triggers = cfg.Triggers ?? new List<ScaleTrigger>();

        // On reconstruit via 'with' (records immutables)
        return cfg with
        {
            Triggers = triggers,
            Behavior = behavior with
            {
                ScaleUp = scaleUp with { Policies = upPolicies },
                ScaleDown = scaleDown with { Policies = downPolicies }
            }
        };
    }


    private static ScaleConfig? GetScaleConfig(
        IDictionary<string, string> annotations,
        string name,
        ILogger<KubernetesService> logger)
    {
        try
        {
            if (annotations.TryGetValue(Scale, out string? annotation) &&
                !string.IsNullOrWhiteSpace(annotation))
            {
                annotation = JsonMinifier.MinifyJson(annotation);
                if (!string.IsNullOrEmpty(annotation))
                {
                    // AOT-safe : on n'utilise QUE le contexte source-généré
                    var opts = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        TypeInfoResolver = ScaleConfigSerializerContext.Default
                    };
                    // Enums en string (insensible à la casse) — AOT OK
                    opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));

                    var parsed = JsonSerializer.Deserialize<ScaleConfig>(annotation, opts);

                    // IMPORTANT : on normalise pour injecter les defaults si des sous-objets manquent
                    return NormalizeScaleConfig(parsed);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "name: {Name}\n annotations[Scale]: {Annotation}", name, annotations.TryGetValue(Scale, out var a) ? a : "<missing>");
        }

        return null;
    }

    public async Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request)
    {
        try
        {
            k8s.Kubernetes client = _client;
            string patchString = $"{{\"spec\": {{\"replicas\": {request.Replicas}}}}}";
            StringContent httpContent = new(patchString, Encoding.UTF8, "application/merge-patch+json");
            // we need to get the base uri, as it's not set on the HttpClient
            switch (request.PodType)
            {
                case PodType.Deployment:
                    {
                        string url = string.Concat(client.BaseUri,
                            $"apis/apps/v1/namespaces/{request.Namespace}/deployments/{request.Deployment}/scale");
                        HttpRequestMessage httpRequest = new(HttpMethod.Patch,
                            new Uri(url));
                        httpRequest.Content = httpContent;
                        if (client.Credentials != null)
                        {
                            await client.Credentials.ProcessHttpRequestAsync(httpRequest, CancellationToken.None);
                        }

                        HttpResponseMessage response =
                            await client.HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                        if (response.StatusCode != HttpStatusCode.OK)
                        {
                            throw new HttpOperationException("Error while scaling deployment");
                        }

                        break;
                    }
                case PodType.StatefulSet:
                    {
                        string url = string.Concat(client.BaseUri,
                            $"apis/apps/v1/namespaces/{request.Namespace}/statefulsets/{request.Deployment}/scale");
                        HttpRequestMessage httpRequest = new(HttpMethod.Patch,
                            new Uri(url));
                        httpRequest.Content = httpContent;
                        if (client.Credentials != null)
                        {
                            await client.Credentials.ProcessHttpRequestAsync(httpRequest, CancellationToken.None);
                        }

                        HttpResponseMessage response =
                            await client.HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                        if (response.StatusCode != HttpStatusCode.OK)
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

    static string? FindServiceNameForPod(V1Pod pod, V1ServiceList? services)
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

        public async Task<SlimFaasJobConfiguration?> ListJobsConfigurationAsync(string kubeNamespace)
    {
        try
        {
            k8s.Kubernetes client = _client;

            Task<V1CronJobList>? cronJobListTask = client.ListNamespacedCronJobAsync(kubeNamespace);

            V1CronJobList? cronJobList = await cronJobListTask;

            return ExtractJobConfigurations(cronJobList);
        }
        catch (HttpOperationException e)
        {
            _logger.LogError(e, "Error while listing kubernetes cron jobs");
            return null;
        }
    }

    public async Task CreateJobAsync(string kubeNamespace, string name, CreateJob createJob, string elementId, string jobFullName, long inQueueTimestamp)
    {
        k8s.Kubernetes client = _client;

        Dictionary<string, ResourceQuantity> requests = new()
        {
            { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("512Mi") }
        };
        CreateJobResources? createJobResources = createJob.Resources;
        if (createJobResources?.Requests != null)
        {
            requests = createJobResources.Requests.ToDictionary(r => r.Key, r => new ResourceQuantity(r.Value));
        }

        Dictionary<string, ResourceQuantity> limits = requests;
        if (createJobResources?.Limits != null)
        {
            limits = createJobResources.Limits.ToDictionary(r => r.Key, r => new ResourceQuantity(r.Value));
        }

        List<V1EnvVar>? envVars = createJob.Environments?.Select(e =>
        {
            if (e.SecretRef != null)
            {
                return new V1EnvVar(
                    e.Name,
                    valueFrom: new V1EnvVarSource
                    {
                        SecretKeyRef = new V1SecretKeySelector(
                            name: e.SecretRef.Name,
                            key: e.SecretRef.Key)
                    }
                );
            }

            if (e.ConfigMapRef != null)
            {
                return new V1EnvVar(
                    e.Name,
                    valueFrom: new V1EnvVarSource
                    {
                        ConfigMapKeyRef = new V1ConfigMapKeySelector(
                            name: e.ConfigMapRef.Name,
                            key: e.ConfigMapRef.Key)
                    }
                );
            }

            if (e.FieldRef != null)
            {
                return new V1EnvVar(
                    e.Name,
                    valueFrom: new V1EnvVarSource
                    {
                        FieldRef = new V1ObjectFieldSelector(
                            e.FieldRef.FieldPath)
                    }
                );
            }

            if (e.ResourceFieldRef != null)
            {
                return new V1EnvVar(
                    e.Name,
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

            return new V1EnvVar(e.Name, e.Value);
        }).ToList();

        var annotations = new Dictionary<string, string>();
        if(createJob.DependsOn != null)
        {
            annotations.Add(DependsOn, string.Join(",", createJob.DependsOn));
        }

        V1Job job = new()
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta {
                Name = jobFullName,
                NamespaceProperty = kubeNamespace,
                Annotations = annotations
            },
            Spec = new V1JobSpec
            {
                TtlSecondsAfterFinished = createJob.TtlSecondsAfterFinished,
                Template = new V1PodTemplateSpec
                {
                    Metadata =
                        new V1ObjectMeta
                        {
                            Labels = new Dictionary<string, string>
                            {
                                { SlimfaasJobName, jobFullName },
                                { SlimfaasJobElementId, elementId },
                                { SlimfaasInQueueTimestamp, inQueueTimestamp.ToString() },
                                { SlimfaasJobStartTimestamp, DateTime.UtcNow.Ticks.ToString() }
                            }
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
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = requests, Limits = limits
                                }
                            }
                        },
                        RestartPolicy = createJob.RestartPolicy
                    }
                },
                BackoffLimit = createJob.BackoffLimit
            }
        };

        V1Job? jobResponse = await client.CreateNamespacedJobAsync(job, kubeNamespace);

        Console.WriteLine($"Job created with name: {jobResponse.Metadata.Name}");
    }


    public async Task<IList<Job>> ListJobsAsync(string kubeNamespace)
    {
        List<Job> jobStatus = new();
        k8s.Kubernetes client = _client;
        V1JobList? jobList = await client.ListNamespacedJobAsync(kubeNamespace);
        foreach (V1Job v1Job in jobList)
        {
            V1PodList? pods = await _client.ListNamespacedPodAsync(
                kubeNamespace,
                labelSelector: $"slimfaas-job-name={v1Job.Metadata?.Name ?? ""}"
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
            foreach (V1Pod? pod in pods.Items)
            {
                if (pod.Status.ContainerStatuses == null)
                {
                    continue;
                }

                foreach (V1ContainerStatus? containerStatus in pod.Status.ContainerStatuses)
                {
                    if (containerStatus.State.Waiting is { Reason: "ImagePullBackOff" or "ErrImagePull" })
                    {
                        status = JobStatus.ImagePullBackOff;
                    }
                }
            }

            List<string> dependsOn = new();
            if (v1Job.Metadata?.Annotations != null && v1Job.Metadata?.Annotations.ContainsKey(DependsOn) == true)
            {
                var split = v1Job.Metadata?.Annotations[DependsOn].Split(",");
                if (split != null)
                {
                    dependsOn.AddRange(split);
                }
            }

            jobStatus.Add(new Job(v1Job.Metadata?.Name ?? "",
                status,
                ips,
                dependsOn,
                v1Job.Labels().TryGetValue(SlimfaasJobElementId, out var jobElementId) ? jobElementId : "",
                v1Job.Labels().TryGetValue(SlimfaasInQueueTimestamp, out var jobInQueueTimestamp) ? long.Parse(jobInQueueTimestamp) : 0,

            v1Job.Labels().TryGetValue(SlimfaasJobStartTimestamp, out var jobStartTimestamp) ? long.Parse(jobStartTimestamp) : 0
            ));
        }

        return jobStatus;
    }

    public async Task DeleteJobAsync(string kubeNamespace, string jobName)
    {
        k8s.Kubernetes client = _client;

        string url = string.Concat(
            client.BaseUri,
            $"apis/batch/v1/namespaces/{kubeNamespace}/jobs/{jobName}?propagationPolicy=Foreground");

        HttpRequestMessage httpRequest = new(HttpMethod.Delete, new Uri(url));

        // 2. (body facultatif) : DeleteOptions
        //    Utile si vous voulez, par ex., gracePeriodSeconds = 0
        // var body = """
        //            {"kind":"DeleteOptions","apiVersion":"v1",
        //             "propagationPolicy":"Foreground","gracePeriodSeconds":0}
        //            """;
        // httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (client.Credentials is not null)
            await client.Credentials.ProcessHttpRequestAsync(httpRequest, CancellationToken.None);

        HttpResponseMessage response = await client.HttpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode is not (HttpStatusCode.OK
            or HttpStatusCode.Accepted
            or HttpStatusCode.NoContent))
        {
            throw new HttpOperationException(
                $"Erreur pendant la suppression du Job {jobName} : {(int)response.StatusCode} {response.ReasonPhrase}");
        }
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
                            : 10
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
                            : 10);

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

        private static SlimFaasJobConfiguration? ExtractJobConfigurations(V1CronJobList cronJobList)
    {
        Dictionary<string, SlimfaasJob> jobs = new();

        foreach (V1CronJob? cronJob in cronJobList.Items)
        {
            if (cronJob is null)
            {
                continue;
            }

            var annotations = cronJob.Metadata?.Annotations ?? new Dictionary<string, string>();
            V1Container? container = cronJob.Spec.JobTemplate.Spec.Template.Spec.Containers.FirstOrDefault();

            bool isSlimfaasJob = annotations.TryGetValue(Job, out var labelValue) &&
                                 bool.TryParse(labelValue, out var isJob) && isJob;
            if (!isSlimfaasJob)
                continue;


            var name = cronJob.Metadata?.Name ?? "unknown";
            var image = container?.Image ?? "";

            var imagesWhitelist = annotations.TryGetValue(JobImagesWhitelist, out var whitelist)
                ? whitelist.Split(',').Select(s => s.Trim()).ToList()
                : new List<string>();

            CreateJobResources? resources = new (
                container?.Resources.Requests?.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()) ??
                new Dictionary<string, string> { { "cpu", "100m" }, { "memory", "100Mi" } },
                container?.Resources.Limits?.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()) ??
                new Dictionary<string, string> { { "cpu", "100m" }, { "memory", "100Mi" } }
            );

            List<string>? dependsOn = annotations.TryGetValue(DependsOn, out var dependsOnValue)
                ? dependsOnValue.Split(',').Select(s => s.Trim()).ToList()
                : null;

            List<EnvVarInput>? environments = null;

            if (container?.Env != null)
            {
                environments = container.Env?.Select(e => new EnvVarInput(
                    Name: e.Name,
                    Value: e.Value ?? "",
                    SecretRef: e.ValueFrom?.SecretKeyRef != null
                        ? new SecretRef(e.ValueFrom.SecretKeyRef.Name, e.ValueFrom.SecretKeyRef.Key)
                        : null,
                    ConfigMapRef: e.ValueFrom?.ConfigMapKeyRef != null
                        ? new ConfigMapRef(e.ValueFrom.ConfigMapKeyRef.Name, e.ValueFrom.ConfigMapKeyRef.Key)
                        : null,
                    FieldRef: e.ValueFrom?.FieldRef != null
                        ? new FieldRef(e.ValueFrom.FieldRef.FieldPath)
                        : null,
                    ResourceFieldRef: e.ValueFrom?.ResourceFieldRef != null
                        ? new ResourceFieldRef(
                            e.ValueFrom.ResourceFieldRef.ContainerName,
                            e.ValueFrom.ResourceFieldRef.Resource,
                            e.ValueFrom.ResourceFieldRef.Divisor?.ToString() ?? "")
                        : null
                )).ToList();
            }

            var visibility = annotations.TryGetValue(DefaultVisibility, out var vis) ? vis : nameof(FunctionVisibility.Private);

            jobs[name] = new SlimfaasJob(
                Image: image,
                ImagesWhitelist: imagesWhitelist,
                Resources: resources,
                DependsOn: dependsOn,
                Environments: environments,
                BackoffLimit: 1,
                Visibility: visibility,
                NumberParallelJob: 1,
                TtlSecondsAfterFinished: 60,
                RestartPolicy: "Never"
            );

            Console.WriteLine("JobConfiguration: ");
            Console.WriteLine(jobs[name]);
        }

        if (jobs.Count != 0)
        {
            return new SlimFaasJobConfiguration(jobs);
        }

        Console.WriteLine("No SlimFaas job configurations found in the cluster.");
        return null;


    }
}
