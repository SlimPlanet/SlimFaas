// File: DockerService.cs
// .NET 9 / AOT-friendly — pure Docker REST (also works with Podman compat API)

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SlimFaas.Kubernetes
{
    [ExcludeFromCodeCoverage]
    public class DockerService : IKubernetesService
    {
        // ---- SlimFaas keys reused for parity with KubernetesService ----
        private const string ReplicasMin = "SlimFaas/ReplicasMin";
        private const string Schedule = "SlimFaas/Schedule";
        private const string Configuration = "SlimFaas/Configuration";
        private const string Function = "slimfaas/function";          // ← label key for functions
        private const string FunctionTrue = "true";
        private const string AppLabel = "app";                        // group containers by "deployment"
        private const string NamespaceLabel = "slimfaas/namespace";

        private const string ReplicasAtStart = "SlimFaas/ReplicasAtStart";
        private const string DependsOn = "SlimFaas/DependsOn";
        private const string SubscribeEvents = "SlimFaas/SubscribeEvents";
        private const string DefaultVisibility = "SlimFaas/DefaultVisibility";
        private const string PathsStartWithVisibility = "SlimFaas/PathsStartWithVisibility";
        private const string ReplicasStartAsSoonAsOneFunctionRetrieveARequest = "SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest";
        private const string TimeoutSecondBeforeSetReplicasMin = "SlimFaas/TimeoutSecondBeforeSetReplicasMin";
        private const string NumberParallelRequest = "SlimFaas/NumberParallelRequest";
        private const string DefaultTrust = "SlimFaas/DefaultTrust";

        private const string SlimfaasJobName = "slimfaas-job-name";
        private const string SlimfaasJobElementId = "slimfaas-job-element-id";
        private const string SlimfaasInQueueTimestamp = "slimfaas-in-queue-timestamp";
        private const string SlimfaasJobStartTimestamp = "slimfaas-job-start-timestamp";

        private readonly ILogger<DockerService> _logger;
        private readonly HttpClient _http;
        private readonly string _apiPrefix; // e.g. "/v1.43" negotiated at startup

        public DockerService(ILogger<DockerService> logger, string? dockerHost = null)
        {
            _logger = logger;

            // Resolve DOCKER_HOST or provided override
            var host = dockerHost ?? Environment.GetEnvironmentVariable("DOCKER_HOST");
            if (string.IsNullOrWhiteSpace(host))
            {
                // default to local unix socket (Linux/macOS) or TCP on Windows
                host = OperatingSystem.IsWindows()
                    ? "http://127.0.0.1:2375" // recommend TCP on Windows for AOT
                    : "unix:///var/run/docker.sock";
            }

            _http = CreateHttpClientForDocker(host, out var baseAddress);
            _http.BaseAddress = baseAddress;

            // Negotiate API version (fall back to v1.43 if unavailable)
            _apiPrefix = TryGetApiVersionAsync(_http).GetAwaiter().GetResult();
        }

        // ------------------ IKubernetesService ------------------

        public async Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request)
        {
            string deployment = request.Deployment;
            string ns = request.Namespace;
            int desired = request.Replicas;

            var containers = await ListContainersByLabelsAsync(new Dictionary<string, string>
            {
                [Function] = FunctionTrue,
                [AppLabel] = deployment,
                [NamespaceLabel] = ns
            }, all: true);

            var running = containers.Where(c => c.State == "running").ToList();
            int current = running.Count;

            if (current < desired)
            {
                // scale up from a template (any existing container from the group)
                var template = containers.FirstOrDefault()
                               ?? throw new InvalidOperationException($"No template container found for deployment '{deployment}' in namespace '{ns}'. Create one instance first or provide an image mapping.");

                var templateInspect = await InspectContainerAsync(template.ID);
                for (int i = 0; i < desired - current; i++)
                    await CreateAndStartReplicaFromTemplateAsync(deployment, ns, templateInspect);
            }
            else if (current > desired)
            {
                foreach (var c in running.Take(current - desired))
                {
                    await StopContainerIfRunningAsync(c.ID);
                    await RemoveContainerAsync(c.ID, force: true);
                }
            }

            return request;
        }

        public async Task<DeploymentsInformations> ListFunctionsAsync(string kubeNamespace, DeploymentsInformations previousDeployments)
        {
            var containers = await ListContainersByLabelsAsync(new Dictionary<string, string>
            {
                [Function] = FunctionTrue,
                [NamespaceLabel] = kubeNamespace
            }, all: true);

            var groups = containers.GroupBy(c => c.Labels != null && c.Labels.TryGetValue(AppLabel, out var d) ? d : "");

            var deployments = new List<DeploymentInformation>();
            var allPods = new List<PodInformation>();

            foreach (var grp in groups)
            {
                var deploymentName = grp.Key;
                if (string.IsNullOrWhiteSpace(deploymentName)) continue;

                // Ne compter que les RUNNING pour les replicas et les pods
                var running = grp.Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase)).ToList();

                var pods = new List<PodInformation>();
                foreach (var c in running)
                {
                    var insp = await TryInspectContainerAsync(c.ID);
                    if (insp is null) continue; // supprimé entre-temps → on ignore

                    var name = TrimSlash(insp.Name) ?? (c.Names?.FirstOrDefault() is string n ? TrimSlash(n) : c.ID[..12]);
                    bool ready = insp.State?.Running == true &&
                                 (insp.State.Health == null || string.Equals(insp.State.Health.Status, "healthy", StringComparison.OrdinalIgnoreCase));

                    string ip = ExtractFirstIPAddress(insp);
                    var ports = ExtractPrivatePorts(insp);

                    pods.Add(new PodInformation(
                        Name: name ?? c.ID[..12],
                        Started: insp.State?.StartedAt is not null,
                        Ready: ready,
                        Ip: ip,
                        DeploymentName: deploymentName,
                        Ports: ports,
                        ResourceVersion: insp.Created?.ToUniversalTime().Ticks.ToString() ?? DateTime.UtcNow.Ticks.ToString()
                    ));
                }

                allPods.AddRange(pods);

                // Récupérer la config/annotations depuis le premier conteneur du groupe (même s’il n’est pas running)
                var first = grp.First();
                var labels = first.Labels ?? new Dictionary<string, string>();

                var config = ParseOrDefault(labels, Configuration, () => new SlimFaasConfiguration(),
                    (json) => JsonSerializer.Deserialize(json, SlimFaasConfigurationSerializerContext.Default.SlimFaasConfiguration) ?? new SlimFaasConfiguration());

                var replicas = running.Count;                        // 👈 uniquement RUNNING
                var endpointReady = pods.Any(p => (p.Ports?.Count ?? 0) > 0);

                var di = new DeploymentInformation(
                    Deployment: deploymentName,
                    Namespace: kubeNamespace,
                    Pods: pods,
                    Configuration: config,
                    Replicas: replicas,
                    ReplicasAtStart: GetInt(labels, ReplicasAtStart, 1),
                    ReplicasMin: GetInt(labels, ReplicasMin, 0),
                    TimeoutSecondBeforeSetReplicasMin: GetInt(labels, TimeoutSecondBeforeSetReplicasMin, 300),
                    NumberParallelRequest: GetInt(labels, NumberParallelRequest, 10),
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest: GetBool(labels, ReplicasStartAsSoonAsOneFunctionRetrieveARequest, false),
                    PodType: PodType.Deployment,
                    DependsOn: SplitCsv(labels, DependsOn),
                    Schedule: ParseOrDefault(labels, Schedule, () => new ScheduleConfig(),
                        (json) => JsonSerializer.Deserialize(json, ScheduleConfigSerializerContext.Default.ScheduleConfig) ?? new ScheduleConfig()),
                    SubscribeEvents: GetSubscribeEvents(labels),
                    Visibility: ParseEnum(labels, DefaultVisibility, FunctionVisibility.Public),
                    PathsStartWithVisibility: GetPathsStartWithVisibility(labels),
                    ResourceVersion: $"{replicas}-{pods.Count}-{endpointReady}",
                    EndpointReady: endpointReady,
                    Trust: ParseEnum(labels, DefaultTrust, FunctionTrust.Trusted)
                );

                deployments.Add(di);
            }

            // AVANT le return, après avoir rempli deployments + allPods :
            var slimFaasPods = await GetSlimFaasSelfPodsAsync();
            var slimfaasInfo = new SlimFaasDeploymentInformation(slimFaasPods.Count, slimFaasPods);

            // (facultatif) on ajoute aussi ces pods à la vue globale
            allPods.AddRange(slimFaasPods);

            // puis :
            return new DeploymentsInformations(deployments, slimfaasInfo, allPods);
        }

        private static string ExtractFirstIPAddress(InspectContainerResponse insp)
        {
            // Prefer user-defined networks first
            var nets = insp.NetworkSettings?.Networks;
            if (nets is { Count: > 0 })
            {
                var first = nets.Values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first?.IPAddress)) return first!.IPAddress!;
            }
            // Legacy IP
            if (!string.IsNullOrWhiteSpace(insp.NetworkSettings?.IPAddress)) return insp.NetworkSettings!.IPAddress!;
            return "";
        }

        private static List<int> ExtractPrivatePorts(InspectContainerResponse insp)
        {
            var ports = new List<int>();
            if (insp.NetworkSettings?.Ports != null)
            {
                foreach (var kv in insp.NetworkSettings.Ports)
                {
                    // key example: "5000/tcp"
                    var key = kv.Key;
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    var idx = key.IndexOf('/');
                    if (idx <= 0) continue;
                    if (int.TryParse(key.AsSpan(0, idx), out var p)) ports.Add(p);
                }
            }
            return ports.Distinct().ToList();
        }

        public async Task CreateJobAsync(string kubeNamespace, string name, CreateJob createJob, string elementId, string jobFullName, long inQueueTimestamp)
        {
            var labels = new Dictionary<string, string>
            {
                [SlimfaasJobName] = jobFullName,
                [SlimfaasJobElementId] = elementId,
                [SlimfaasInQueueTimestamp] = inQueueTimestamp.ToString(CultureInfo.InvariantCulture),
                [SlimfaasJobStartTimestamp] = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
                [Function] = "false",
                [NamespaceLabel] = kubeNamespace
            };
            if (createJob.DependsOn is { Count: > 0 })
                labels[DependsOn] = string.Join(",", createJob.DependsOn);

            var env = (createJob.Environments ?? Array.Empty<EnvVarInput>())
                .Where(e => e.SecretRef == null && e.ConfigMapRef == null && e.FieldRef == null && e.ResourceFieldRef == null)
                .Select(e => $"{e.Name}={e.Value}")
                .ToArray();

            var hostConfig = new CreateContainer_HostConfig
            {
                AutoRemove = true
            };
            if (createJob.Resources?.Limits is not null)
            {
                if (createJob.Resources.Limits.TryGetValue("memory", out var mem) && TryParseMemoryBytes(mem, out long bytes))
                    hostConfig.Memory = bytes;
                if (createJob.Resources.Limits.TryGetValue("cpu", out var cpu))
                    ApplyCpuLimit(hostConfig, cpu);
            }

            await TryPullImageAsync(createJob.Image);

            var createBody = new CreateContainerRequest
            {
                Image = createJob.Image,
                Name = jobFullName,
                Labels = labels,
                Env = env,
                Cmd = createJob.Args?.ToArray() ?? Array.Empty<string>(),
                HostConfig = hostConfig
            };

            var createRes = await PostJsonAsync<CreateContainerRequest, CreateContainerResponse>(
                $"{_apiPrefix}/containers/create?name={Uri.EscapeDataString(jobFullName)}",
                createBody,
                DockerJson.Default.CreateContainerRequest,
                DockerJson.Default.CreateContainerResponse);

            var id = createRes?.Id ?? throw new InvalidOperationException("Docker returned no container id for job creation.");
            await PostAsync($"{_apiPrefix}/containers/{id}/start", null);
        }

        public async Task<IList<Job>> ListJobsAsync(string kubeNamespace)
        {
            var labelDict = new Dictionary<string, bool>
            {
                [SlimfaasJobName] = true,                       // présence du label
                [$"{NamespaceLabel}={kubeNamespace}"] = true    // label égal à la valeur
            };
            var filter = new FiltersLabel(labelDict);
            string filterJson = JsonSerializer.Serialize(filter, DockerJson.Default.FiltersLabel);
            string url = $"{_apiPrefix}/containers/json?all=1&filters={WebUtility.UrlEncode(filterJson)}";

            var containers = await GetAsync(url, DockerJson.Default.ListContainerSummary) ?? new List<ContainerSummary>();

            var result = new List<Job>();
            foreach (var c in containers)
            {
                var insp = await TryInspectContainerAsync(c.ID);
                if (insp is null) continue;

                var ips = new List<string>();
                var ip = ExtractFirstIPAddress(insp);
                if (!string.IsNullOrWhiteSpace(ip)) ips.Add(ip!);

                JobStatus status = JobStatus.Pending;
                if (insp.State?.Running == true) status = JobStatus.Running;
                else if (insp.State?.Running == false && insp.State?.ExitCode == 0) status = JobStatus.Succeeded;
                else if (insp.State?.Running == false && insp.State?.ExitCode != 0) status = JobStatus.Failed;

                if (!string.IsNullOrWhiteSpace(insp.State?.Error) &&
                    insp.State!.Error!.IndexOf("pull", StringComparison.OrdinalIgnoreCase) >= 0)
                    status = JobStatus.ImagePullBackOff;

                var labels = insp.Config?.Labels ?? new Dictionary<string, string>();
                var dependsOn = SplitCsv(labels, DependsOn);
                labels.TryGetValue(SlimfaasJobElementId, out var elementId);
                long.TryParse(labels.GetValueOrDefault(SlimfaasInQueueTimestamp), out var inQueue);
                long.TryParse(labels.GetValueOrDefault(SlimfaasJobStartTimestamp), out var startTs);

                result.Add(new Job(
                    Name: TrimSlash(insp.Name) ?? c.Names?.FirstOrDefault()?.Trim('/') ?? c.ID[..12],
                    Status: status,
                    Ips: ips,
                    DependsOn: dependsOn,
                    ElementId: elementId ?? "",
                    InQueueTimestamp: inQueue,
                    StartTimestamp: startTs
                ));
            }

            return result;
        }

        public async Task DeleteJobAsync(string kubeNamespace, string jobName)
        {
            var id = await ResolveContainerIdByNameAsync(jobName);
            if (id is null)
            {
                _logger.LogWarning("DeleteJob: container '{JobName}' not found.", jobName);
                return;
            }

            await StopContainerIfRunningAsync(id);
            await RemoveContainerAsync(id, force: true);
        }

        // ------------------ Internal helpers ------------------

        private async Task CreateAndStartReplicaFromTemplateAsync(string deployment, string ns, InspectContainerResponse template)
        {
            var labels = new Dictionary<string, string>(template.Config?.Labels ?? new Dictionary<string, string>())
            {
                [Function] = FunctionTrue,
                [AppLabel] = deployment,
                [NamespaceLabel] = ns
            };

            var env = template.Config?.Env ?? Array.Empty<string>();
            var image = template.Config?.Image ?? template.Image;
            var cmd = template.Config?.Cmd ?? Array.Empty<string>();

            if (!string.IsNullOrWhiteSpace(image))
                await TryPullImageAsync(image!);

            var name = $"{deployment}-{Guid.NewGuid():N}".Substring(0, 20);

            var createBody = new CreateContainerRequest
            {
                Image = image,
                Name = name,
                Labels = labels,
                Env = env,
                Cmd = cmd,
                HostConfig = new CreateContainer_HostConfig { AutoRemove = true }
            };

            if (template.NetworkSettings?.Ports is { Count: > 0 })
            {
                var exposed = new Dictionary<string, object>();
                foreach (var kv in template.NetworkSettings.Ports)
                    if (!string.IsNullOrWhiteSpace(kv.Key)) exposed[kv.Key] = new { };
                createBody.ExposedPorts = exposed;
            }

            var createRes = await PostJsonAsync<CreateContainerRequest, CreateContainerResponse>(
                $"{_apiPrefix}/containers/create?name={Uri.EscapeDataString(name)}",
                createBody,
                DockerJson.Default.CreateContainerRequest,
                DockerJson.Default.CreateContainerResponse);

            var id = createRes?.Id ?? throw new InvalidOperationException("Create container returned no Id.");
            await PostAsync($"{_apiPrefix}/containers/{id}/start", null);
        }

        private async Task<IList<PodInformation>> GetSlimFaasSelfPodsAsync()
        {
            var pods = new List<PodInformation>();

            // 1) Essayer via le vrai hostname Linux (short container ID)
            try
            {
                var selfKey = System.Net.Dns.GetHostName();       // ex: "a1b2c3d4e5f6"
                var insp = await InspectContainerAsync(selfKey);   // id prefix OK
                pods.Add(ToPod(insp, "slimfaas"));
                return pods;
            }
            catch
            {
                // ignore → fallback
            }

            // 2) Fallback: conteneurs RUNNING du service compose "slimfaas"
            var labelDict = new Dictionary<string, bool> { ["com.docker.compose.service=slimfaas"] = true };
            var filter = new FiltersLabel(labelDict);
            var json = JsonSerializer.Serialize(filter, DockerJson.Default.FiltersLabel);
            var url = $"{_apiPrefix}/containers/json?all=0&filters={WebUtility.UrlEncode(json)}"; // all=0 => running only

            var containers = await GetAsync(url, DockerJson.Default.ListContainerSummary) ?? new List<ContainerSummary>();
            foreach (var c in containers)
            {
                var insp = await InspectContainerAsync(c.ID);
                pods.Add(ToPod(insp, "slimfaas"));
            }

            return pods;
        }

        private static PodInformation ToPod(InspectContainerResponse insp, string deploymentName)
        {
            return new PodInformation(
                Name: TrimSlash(insp.Name) ?? insp.Id[..12],
                Started: insp.State?.StartedAt is not null,
                Ready: insp.State?.Running == true &&
                       (insp.State.Health == null || string.Equals(insp.State.Health.Status, "healthy", StringComparison.OrdinalIgnoreCase)),
                Ip: ExtractFirstIPAddress(insp),
                DeploymentName: deploymentName,
                Ports: ExtractPrivatePorts(insp),
                ResourceVersion: insp.Created?.ToUniversalTime().Ticks.ToString() ?? DateTime.UtcNow.Ticks.ToString()
            );
        }


        private async Task<List<ContainerSummary>> ListContainersByLabelsAsync(
            Dictionary<string, string> labels, bool all)
        {
            // Convertit "k"->"v" en "k=v"
            var labelDict = new Dictionary<string, bool>();
            foreach (var kv in labels)
                labelDict[$"{kv.Key}={kv.Value}"] = true;

            var filter = new FiltersLabel(labelDict);
            string filterJson = JsonSerializer.Serialize(filter, DockerJson.Default.FiltersLabel);
            string url = $"{_apiPrefix}/containers/json?all={(all ? 1 : 0)}&filters={WebUtility.UrlEncode(filterJson)}";

            var list = await GetAsync(url, DockerJson.Default.ListContainerSummary);
            return list ?? new List<ContainerSummary>();
        }


        private async Task<InspectContainerResponse> InspectContainerAsync(string id)
        {
            var resp = await GetAsync($"{_apiPrefix}/containers/{id}/json", DockerJson.Default.InspectContainerResponse)
                       ?? throw new InvalidOperationException($"Cannot inspect container {id}");
            return resp;
        }

        private Task<InspectContainerResponse?> TryInspectContainerAsync(string id)
        {
            // GetAsync(...) renvoie null si status != 2xx → pas d’exception
            return GetAsync($"{_apiPrefix}/containers/{id}/json", DockerJson.Default.InspectContainerResponse);
        }

        private async Task StopContainerIfRunningAsync(string id)
        {
            try { await PostAsync($"{_apiPrefix}/containers/{id}/stop", null); }
            catch (HttpRequestException) { /* ignore */ }
        }

        private async Task RemoveContainerAsync(string id, bool force)
        {
            await DeleteAsync($"{_apiPrefix}/containers/{id}?force={(force ? "true" : "false")}");
        }

        private async Task<string?> ResolveContainerIdByNameAsync(string name)
        {
            var filter = new FiltersName(new Dictionary<string, bool> { [name] = true });
            string filterJson = JsonSerializer.Serialize(filter, DockerJson.Default.FiltersName);
            string url = $"{_apiPrefix}/containers/json?all=1&filters={WebUtility.UrlEncode(filterJson)}";

            var list = await GetAsync(url, DockerJson.Default.ListContainerSummary);
            return list?.FirstOrDefault()?.ID;
        }

        private async Task TryPullImageAsync(string image)
        {
            if (string.IsNullOrWhiteSpace(image)) return;

            var imageName = image;
            var tag = "latest";
            var idx = image.LastIndexOf(':');
            if (idx > 0 && idx < image.Length - 1 && !image.Contains('@'))
            {
                imageName = image[..idx];
                tag = image[(idx + 1)..];
            }

            try
            {
                var path = $"{_apiPrefix}/images/create?fromImage={Uri.EscapeDataString(imageName)}&tag={Uri.EscapeDataString(tag)}";
                using var req = new HttpRequestMessage(HttpMethod.Post, path);
                req.Content = new ByteArrayContent(Array.Empty<byte>());
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                res.EnsureSuccessStatusCode();
                _ = await res.Content.ReadAsByteArrayAsync(); // drain progress
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Image pull failed or skipped for {Image}", image);
            }
        }

        // ------------------ JSON helpers & mapping ------------------

        private static string? TrimSlash(string? s) => s?.Length > 0 && s[0] == '/' ? s[1..] : s;

        private static int GetInt(IReadOnlyDictionary<string, string> labels, string key, int defVal)
            => labels.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : defVal;

        private static bool GetBool(IReadOnlyDictionary<string, string> labels, string key, bool defVal)
            => labels.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : defVal;

        private static IList<string> SplitCsv(IReadOnlyDictionary<string, string> labels, string key)
        {
            if (!labels.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return Array.Empty<string>();
            return v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static TEnum ParseEnum<TEnum>(IReadOnlyDictionary<string,string> labels, string key, TEnum defVal) where TEnum : struct
        {
            if (labels.TryGetValue(key, out var v) && Enum.TryParse<TEnum>(v, true, out var parsed))
                return parsed;
            return defVal;
        }

        private static T ParseOrDefault<T>(IReadOnlyDictionary<string,string> labels, string key, Func<T> defFactory, Func<string,T> parser)
        {
            if (labels.TryGetValue(key, out var json) && !string.IsNullOrWhiteSpace(json))
            {
                try { return parser(json); } catch { /* ignore parse issue */ }
            }
            return defFactory();
        }

        private static IList<PathVisibility> GetPathsStartWithVisibility(IReadOnlyDictionary<string, string> labels)
        {
            if (!labels.TryGetValue(PathsStartWithVisibility, out var raw) || string.IsNullOrWhiteSpace(raw))
                return Array.Empty<PathVisibility>();

            var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<PathVisibility>(tokens.Length);

            foreach (var token in tokens)
            {
                var parts = token.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                FunctionVisibility vis = FunctionVisibility.Public;
                string path;

                if (parts.Length == 2)
                {
                    if (parts[0].Equals("Private", StringComparison.OrdinalIgnoreCase)) vis = FunctionVisibility.Private;
                    else if (parts[0].Equals("Public", StringComparison.OrdinalIgnoreCase)) vis = FunctionVisibility.Public;
                    path = parts[1];
                }
                else path = parts[0];

                list.Add(new PathVisibility(path, vis));
            }

            return list;
        }

        private static IList<SubscribeEvent> GetSubscribeEvents(IReadOnlyDictionary<string, string> labels)
        {
            if (!labels.TryGetValue(SubscribeEvents, out var raw) || string.IsNullOrWhiteSpace(raw))
                return Array.Empty<SubscribeEvent>();

            var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<SubscribeEvent>(tokens.Length);

            foreach (var token in tokens)
            {
                var parts = token.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                FunctionVisibility vis = FunctionVisibility.Public;
                string name;

                if (parts.Length == 2)
                {
                    if (parts[0].Equals("Private", StringComparison.OrdinalIgnoreCase)) vis = FunctionVisibility.Private;
                    else if (parts[0].Equals("Public", StringComparison.OrdinalIgnoreCase)) vis = FunctionVisibility.Public;
                    name = parts[1];
                }
                else name = parts[0];

                list.Add(new SubscribeEvent(name, vis));
            }

            return list;
        }

        private static void ApplyCpuLimit(CreateContainer_HostConfig hc, string cpu)
        {
            // Accept "500m", "0.5", "1", "2"
            if (cpu.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                var num = cpu[..^1];
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var milli))
                {
                    hc.CPUPeriod = 100_000; // 100ms
                    hc.CPUQuota  = (long)(milli / 1000.0 * 100_000.0);
                }
                return;
            }

            if (double.TryParse(cpu, NumberStyles.Float, CultureInfo.InvariantCulture, out var cores))
            {
                hc.CPUPeriod = 100_000;
                hc.CPUQuota  = (long)(cores * 100_000.0);
            }
        }

        private static bool TryParseMemoryBytes(string value, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            long mul = 1;

            if (value.EndsWith("Ki", StringComparison.OrdinalIgnoreCase)) { mul = 1024; value = value[..^2]; }
            else if (value.EndsWith("Mi", StringComparison.OrdinalIgnoreCase)) { mul = 1024L * 1024; value = value[..^2]; }
            else if (value.EndsWith("Gi", StringComparison.OrdinalIgnoreCase)) { mul = 1024L * 1024 * 1024; value = value[..^2]; }
            else if (value.EndsWith("Ti", StringComparison.OrdinalIgnoreCase)) { mul = 1024L * 1024 * 1024 * 1024; value = value[..^2]; }
            else if (value.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { mul = 1000; value = value[..^1]; }
            else if (value.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { mul = 1000L * 1000; value = value[..^1]; }
            else if (value.EndsWith("G", StringComparison.OrdinalIgnoreCase)) { mul = 1000L * 1000 * 1000; value = value[..^1]; }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return false;
            bytes = (long)(num * mul);
            return true;
        }

        // ------------------ HTTP plumbing (AOT-friendly) ------------------

        private static HttpClient CreateHttpClientForDocker(string? dockerHost, out Uri baseAddress)
{
    // 1) Sources possibles (ordre de priorité)
    string? candidate =
        dockerHost
        ?? Environment.GetEnvironmentVariable("SLIMFAAS_DOCKER_HOST")
        ?? Environment.GetEnvironmentVariable("DOCKER_HOST");

    // 2) Si on est dans un conteneur Linux et que /var/run/docker.sock existe, on force le socket Unix
    if (OperatingSystem.IsLinux() && File.Exists("/var/run/docker.sock"))
    {
        candidate = "unix:///var/run/docker.sock";
    }

    // 3) Fallback par plateforme
    if (string.IsNullOrWhiteSpace(candidate))
    {
        candidate = OperatingSystem.IsWindows()
            ? "http://127.0.0.1:2375"
            : "unix:///var/run/docker.sock";
    }

    // 4) Construction du HttpClient
    if (candidate.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
    {
        var path = candidate.Replace("unix://", "", StringComparison.OrdinalIgnoreCase);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var ep = new UnixDomainSocketEndPoint(path);
                var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await sock.ConnectAsync(ep, ct);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
                {
                    // Message plus explicite
                    throw new HttpRequestException(
                        $"Permission denied opening unix socket '{path}'. " +
                        $"Run container as root or add the docker socket GID to the process.", ex);
                }
                return new NetworkStream(sock, ownsSocket: true);
            }
        };
        baseAddress = new Uri("http://localhost"); // hôte bidon requis par HttpClient
        return new HttpClient(handler);
    }

    // TCP (http/https)
    if (!candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        candidate = "http://" + candidate.Trim();

    var uri = new Uri(candidate);

    // 5) Si l’URL est http(s) sans port explicite, on met un port par défaut Docker Desktop
    if (uri.Scheme == Uri.UriSchemeHttp && (uri.IsDefaultPort || uri.Port == 80))
        uri = new UriBuilder(uri) { Port = 2375 }.Uri;
    else if (uri.Scheme == Uri.UriSchemeHttps && (uri.IsDefaultPort || uri.Port == 443))
        uri = new UriBuilder(uri) { Port = 2376 }.Uri;

    baseAddress = uri;

    var http = new HttpClient(new SocketsHttpHandler
    {
        // En dev, tolère un TLS autosigné. ⚠️ À durcir en prod.
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, __, ___, ____) => true
        }
    });

    return http;
}


        private static async Task<string> TryGetApiVersionAsync(HttpClient http)
        {
            try
            {
                var ver = await http.GetFromJsonAsync("/version", DockerJson.Default.DockerVersionResponse);
                if (!string.IsNullOrWhiteSpace(ver?.ApiVersion))
                    return $"/v{ver!.ApiVersion}";
            }
            catch
            {
                // ignore, un fallback peut suivre
            }
            return "/v1.43";
        }

        // Typed helpers (pass explicit JsonTypeInfo<T> from source-gen context)

        private async Task<T?> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo)
        {
            using var res = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) return default;
            await using var s = await res.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync(s, typeInfo);
        }

        private async Task<TRes?> PostJsonAsync<TReq, TRes>(
            string path,
            TReq body,
            JsonTypeInfo<TReq> reqTypeInfo,
            JsonTypeInfo<TRes> resTypeInfo)
        {
            var json = JsonSerializer.Serialize(body, reqTypeInfo);
            using var res = await _http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
            res.EnsureSuccessStatusCode();
            await using var s = await res.Content.ReadAsStreamAsync();
            if (s.CanSeek && s.Length == 0) return default;
            return await JsonSerializer.DeserializeAsync(s, resTypeInfo);
        }

        private async Task PostAsync(string path, HttpContent? body)
        {
            using var res = await _http.PostAsync(path, body);
            res.EnsureSuccessStatusCode();
        }

        private async Task DeleteAsync(string path)
        {
            using var res = await _http.DeleteAsync(path);
            res.EnsureSuccessStatusCode();
        }
    }

// ------------------ Minimal Docker API DTOs & source-gen ------------------
}

namespace SlimFaas.Kubernetes
{

    internal record FiltersLabel(
        [property: JsonPropertyName("label")] Dictionary<string, bool> Label
    );

    internal record FiltersName(
        [property: JsonPropertyName("name")] Dictionary<string, bool> Name
    );

    internal record DockerVersionResponse(
        [property: JsonPropertyName("ApiVersion")] string ApiVersion,
        [property: JsonPropertyName("Version")] string Version
    );

    internal record ContainerSummary(
        [property: JsonPropertyName("Id")] string ID,
        [property: JsonPropertyName("Names")] List<string>? Names,
        [property: JsonPropertyName("Image")] string Image,
        [property: JsonPropertyName("State")] string State,
        [property: JsonPropertyName("Status")] string Status,
        [property: JsonPropertyName("Labels")] Dictionary<string,string>? Labels
    );

    internal record InspectContainerResponse(
        [property: JsonPropertyName("Id")] string Id,
        [property: JsonPropertyName("Name")] string? Name,
        [property: JsonPropertyName("Image")] string Image,
        [property: JsonPropertyName("Created")] DateTimeOffset? Created,
        [property: JsonPropertyName("Config")] Inspect_Config? Config,
        [property: JsonPropertyName("State")] Inspect_State? State,
        [property: JsonPropertyName("NetworkSettings")] Inspect_NetworkSettings? NetworkSettings
    );

    internal record Inspect_Config(
        [property: JsonPropertyName("Image")] string? Image,
        [property: JsonPropertyName("Env")] string[]? Env,
        [property: JsonPropertyName("Cmd")] string[]? Cmd,
        [property: JsonPropertyName("Labels")] Dictionary<string,string>? Labels,
        [property: JsonPropertyName("ExposedPorts")] Dictionary<string, object>? ExposedPorts
    );

    internal record Inspect_State(
        [property: JsonPropertyName("Running")] bool Running,
        [property: JsonPropertyName("ExitCode")] int ExitCode,
        [property: JsonPropertyName("Error")] string? Error,
        [property: JsonPropertyName("Health")] Inspect_Health? Health,
        [property: JsonPropertyName("StartedAt")] DateTimeOffset? StartedAt
    );

    internal record Inspect_Health([property: JsonPropertyName("Status")] string Status);

    internal record Inspect_NetworkSettings(
        [property: JsonPropertyName("IPAddress")] string? IPAddress,
        [property: JsonPropertyName("Networks")] Dictionary<string, Inspect_EndpointSettings>? Networks,
        [property: JsonPropertyName("Ports")] Dictionary<string, List<Inspect_PortBinding>?>? Ports
    );

    internal record Inspect_EndpointSettings([property: JsonPropertyName("IPAddress")] string? IPAddress);

    internal record Inspect_PortBinding(
        [property: JsonPropertyName("HostIp")] string? HostIp,
        [property: JsonPropertyName("HostPort")] string? HostPort
    );

    internal record CreateContainerRequest
    {
        [JsonPropertyName("Image")] public string? Image { get; init; }
        [JsonPropertyName("Name")] public string? Name { get; init; }
        [JsonPropertyName("Labels")] public Dictionary<string,string>? Labels { get; init; }
        [JsonPropertyName("Env")] public string[]? Env { get; init; }
        [JsonPropertyName("Cmd")] public string[]? Cmd { get; init; }
        [JsonPropertyName("HostConfig")] public CreateContainer_HostConfig? HostConfig { get; init; }
        [JsonPropertyName("ExposedPorts")] public Dictionary<string, object>? ExposedPorts { get; set; }
    }

    internal record CreateContainer_HostConfig
    {
        [JsonPropertyName("AutoRemove")] public bool AutoRemove { get; set; }
        [JsonPropertyName("Memory")] public long? Memory { get; set; }
        [JsonPropertyName("CpuPeriod")] public long? CPUPeriod { get; set; }
        [JsonPropertyName("CpuQuota")] public long? CPUQuota { get; set; }
    }

    internal record CreateContainerResponse([property: JsonPropertyName("Id")] string Id);

    [JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(DockerVersionResponse))]
    [JsonSerializable(typeof(List<ContainerSummary>))]
    [JsonSerializable(typeof(InspectContainerResponse))]
    [JsonSerializable(typeof(CreateContainerRequest))]
    [JsonSerializable(typeof(CreateContainerResponse))]
    [JsonSerializable(typeof(FiltersLabel))]
    [JsonSerializable(typeof(FiltersName))]
    [JsonSerializable(typeof(object))]
    internal partial class DockerJson : JsonSerializerContext { }
}
