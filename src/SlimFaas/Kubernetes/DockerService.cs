// File: DockerService.cs
// .NET 9 / AOT-friendly — pure Docker REST (also works with Podman compat API)

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Security;
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
        private const string TemplateLabel = "SlimFaas/template";

        // ---- SlimFaas keys reused for parity with KubernetesService ----
        private const string ReplicasMin = "SlimFaas/ReplicasMin";
        private const string Schedule = "SlimFaas/Schedule";
        private const string Configuration = "SlimFaas/Configuration";
        private const string Function = "SlimFaas/Function"; // ← label key for functions
        private const string FunctionTrue = "true";
        private const string AppLabel = "app"; // group containers by "deployment"
        private const string NamespaceLabel = "SlimFaas/Namespace";

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
        private const string PublishedPortsLabel = "SlimFaas/PublishedPorts";

        private const string SlimfaasJobName = "slimfaas-job-name";
        private const string SlimfaasJobElementId = "slimfaas-job-element-id";
        private const string SlimfaasInQueueTimestamp = "slimfaas-in-queue-timestamp";
        private const string SlimfaasJobStartTimestamp = "slimfaas-job-start-timestamp";

        public const string HttpClientName = "DockerServiceHttpClient";
        private const string TtlSecondsAfterFinished = "SlimFaas/TtlSecondsAfterFinished";
        private readonly string _apiPrefix; // e.g. "/v1.43" negotiated at startup
        private readonly HttpClient _http;

        private readonly ILogger<DockerService> _logger;
        private readonly string? _networkName;

        private readonly string? _composeProject;

        public DockerService(IHttpClientFactory httpClientFactory, ILogger<DockerService> logger,
            string? dockerHost = null)
        {
            _logger = logger;

            // Resolve DOCKER_HOST or provided override
            _http = httpClientFactory.CreateClient(HttpClientName);
            if (_http.BaseAddress is null)
            {
                string? host = dockerHost ?? Environment.GetEnvironmentVariable("DOCKER_HOST");
                if (string.IsNullOrWhiteSpace(host))
                {
                    host = OperatingSystem.IsWindows() ? "http://127.0.0.1:2375" : "unix:///var/run/docker.sock";
                }

                /*if (host.StartsWith("unix://"))
                {
                    throw new InvalidOperationException("Unix sockets not supported via IHttpClientFactory. Use TCP.");
                }*/

                // http(s) → utilise comme BaseAddress
                UriBuilder uri = new(host);
                if (uri.Scheme == Uri.UriSchemeHttp && (uri.Port == 80 || uri.Port == -1))
                {
                    uri.Port = 2375;
                }
                else if (uri.Scheme == Uri.UriSchemeHttps && (uri.Port == 443 || uri.Port == -1))
                {
                    uri.Port = 2376;
                }

                _http.BaseAddress = uri.Uri;
            }

            // Negotiate API version (fall back to v1.43 if unavailable)
            _apiPrefix = TryGetApiVersionAsync(_http).GetAwaiter().GetResult();
            _networkName = GetSelfPrimaryNetworkNameAsync().GetAwaiter().GetResult();
            _composeProject = GetComposeProjectAsync().GetAwaiter().GetResult();
        }
        private static bool IsTrueLike(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || v.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || v.Equals("y", StringComparison.OrdinalIgnoreCase);
        }

       public async Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request)
{
    string deployment = request.Deployment;
    string ns = request.Namespace;
    int desired = Math.Max(0, request.Replicas);

    // 1) Récupère tous les conteneurs du "deployment" (running + stopped), présence du label Function
    List<ContainerSummary> all = await ListContainersByLabelsAsync(
        new Dictionary<string, string?> {
            [Function] = null,                 // présence du label
            [AppLabel] = deployment,
            [NamespaceLabel] = ns
        },
        all: true);

    // Post-filtre tolérant sur la valeur du label Function
    all = all.Where(c => c.Labels is not null
                      && c.Labels.TryGetValue(Function, out var v)
                      && IsTrueLike(v)).ToList();

    // 2) Running actuels (comparaison case-insensitive)
    static bool IsRunning(string? s) => s is not null && s.Equals("running", StringComparison.OrdinalIgnoreCase);
    List<ContainerSummary> running = all.Where(c => IsRunning(c.State)).ToList();
    int current = running.Count;

    if (current < desired)
    {
        // -------- Scale UP --------
        // a) Cherche un template existant
        List<ContainerSummary> withTemplate = await ListContainersByLabelsAsync(
            new Dictionary<string, string?> {
                [Function] = null,
                [AppLabel] = deployment,
                [NamespaceLabel] = ns,
                [TemplateLabel] = "true"
            },
            all: true);

        withTemplate = withTemplate.Where(c => c.Labels is not null
                                            && c.Labels.TryGetValue(Function, out var v)
                                            && IsTrueLike(v)).ToList();

        InspectContainerResponse? templateInspect = null;

        if (withTemplate.FirstOrDefault() is { } tpl)
        {
            // Template déjà présent
            templateInspect = await InspectContainerAsync(tpl.ID);
        }
        else
        {
            // Pas de template : on prend une source (running si possible, sinon n'importe laquelle)
            ContainerSummary? source = running.FirstOrDefault() ?? all.FirstOrDefault();
            if (source is null)
                throw new InvalidOperationException($"No container found to clone for '{deployment}' in ns '{ns}'.");

            InspectContainerResponse srcInsp = await InspectContainerAsync(source.ID);

            // On assure un template "carcasse" pour conserver les métadonnées (labels/ports), puis on s’en sert
            await EnsureTemplateContainerAsync(deployment, ns, srcInsp);

            // Re‑lookup du template (optionnel mais propre)
            withTemplate = await ListContainersByLabelsAsync(
                new Dictionary<string, string?> {
                    [Function] = null,
                    [AppLabel] = deployment,
                    [NamespaceLabel] = ns,
                    [TemplateLabel] = "true"
                },
                all: true);
            withTemplate = withTemplate.Where(c => c.Labels is not null
                                                && c.Labels.TryGetValue(Function, out var v)
                                                && IsTrueLike(v)).ToList();

            if (withTemplate.FirstOrDefault() is { } tpl2)
                templateInspect = await InspectContainerAsync(tpl2.ID);
            else
                // À défaut (course condition), on peut cloner directement depuis srcInsp
                templateInspect = srcInsp;
        }

        int toCreate = desired - current;
        for (int i = 0; i < toCreate; i++)
        {
            await CreateAndStartReplicaFromTemplateAsync(deployment, ns, templateInspect);
        }
    }
    else if (current > desired)
    {
        // -------- Scale DOWN --------
        // a) Si on va à 0 → assurer un template, même si plus de running
        if (desired == 0)
        {
            // Cherche si un template existe déjà
            List<ContainerSummary> existingTemplate = await ListContainersByLabelsAsync(
                new Dictionary<string, string?> {
                    [Function] = null,
                    [AppLabel] = deployment,
                    [NamespaceLabel] = ns,
                    [TemplateLabel] = "true"
                },
                all: true);

            existingTemplate = existingTemplate.Where(c => c.Labels is not null
                                                        && c.Labels.TryGetValue(Function, out var v)
                                                        && IsTrueLike(v)).ToList();

            if (existingTemplate.Count == 0)
            {
                // Source pour créer le template : running si possible, sinon n'importe laquelle
                ContainerSummary? source = running.FirstOrDefault() ?? all.FirstOrDefault();
                if (source is not null)
                {
                    InspectContainerResponse? srcInsp = await TryInspectContainerAsync(source.ID);
                    if (srcInsp is not null)
                        await EnsureTemplateContainerAsync(deployment, ns, srcInsp);
                }
            }
        }

        // b) Stop & remove les running en surplus
        foreach (ContainerSummary c in running.Take(current - desired))
        {
            await StopContainerIfRunningAsync(c.ID);
            await RemoveContainerAsync(c.ID, force: true);
        }

        // c) Si desired==0 → fenêtre de course : s'il reste des running, on les arrête/retire
        if (desired == 0)
        {
            List<ContainerSummary> again = await ListContainersByLabelsAsync(
                new Dictionary<string, string?> {
                    [Function] = null,
                    [AppLabel] = deployment,
                    [NamespaceLabel] = ns
                },
                all: true);

            again = again.Where(c => c.Labels is not null
                                  && c.Labels.TryGetValue(Function, out var v)
                                  && IsTrueLike(v)).ToList();

            foreach (ContainerSummary c in again.Where(x => IsRunning(x.State)))
            {
                await StopContainerIfRunningAsync(c.ID);
                await RemoveContainerAsync(c.ID, force: true);
            }
        }
    }

    return request;
}



        public async Task<DeploymentsInformations> ListFunctionsAsync(string kubeNamespace,
            DeploymentsInformations previousDeployments)
        {
            List<ContainerSummary> containers = await ListContainersByLabelsAsync(
                new Dictionary<string, string?> { [Function] = null, [NamespaceLabel] = kubeNamespace }, true);
            containers = containers.Where(c => c.Labels is not null
                                               && c.Labels.TryGetValue(Function, out var v)
                                               && IsTrueLike(v)).ToList();

            // Post-filtre côté C#
            containers = containers.Where(c =>
                c.Labels is not null &&
                c.Labels.TryGetValue(Function, out var v) &&
                IsTrueLike(v)
            ).ToList();

            IEnumerable<IGrouping<string, ContainerSummary>> groups = containers.GroupBy(c =>
                c.Labels != null && c.Labels.TryGetValue(AppLabel, out string? d) ? d : "");

            List<DeploymentInformation> deployments = new();
            List<PodInformation> allPods = new();

            foreach (IGrouping<string, ContainerSummary> grp in groups)
            {
                string deploymentName = grp.Key;
                if (string.IsNullOrWhiteSpace(deploymentName))
                {
                    continue;
                }

                // Ne compter que les RUNNING pour les replicas et les pods
                List<ContainerSummary> running = grp
                    .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase)).ToList();
                _logger.LogDebug("DockerService: Deployment {Deployment} has {Count} running containers",
                    deploymentName, running.Count);
                List<PodInformation> pods = new();
                foreach (ContainerSummary c in running)
                {
                    InspectContainerResponse? insp = await TryInspectContainerAsync(c.ID);
                    if (insp is null)
                    {
                        continue; // supprimé entre-temps → on ignore
                    }

                    string? name = TrimSlash(insp.Name) ??
                                   (c.Names?.FirstOrDefault() is string n ? TrimSlash(n) : c.ID[..12]);
                    bool ready = insp.State?.Running == true &&
                                 (insp.State.Health == null || string.Equals(insp.State.Health.Status, "healthy",
                                     StringComparison.OrdinalIgnoreCase));

                    string ip = ExtractPreferredIPAddress(insp);
                    List<int> ports = GetAllContainerPortsNoHeuristic(insp);
                    _logger.LogDebug("DockerService: Pod {Pod} IP={IP} Ports=[{Ports}] Ready={Ready}",
                        name, ip, string.Join(",", ports), ready);
                    pods.Add(new PodInformation(
                        name ?? c.ID[..12],
                        insp.State?.StartedAt is not null,
                        ready,
                        ip,
                        deploymentName,
                        ports,
                        insp.Created?.ToUniversalTime().Ticks.ToString() ?? DateTime.UtcNow.Ticks.ToString()
                    ));
                }

                allPods.AddRange(pods);

                // Récupérer la config/annotations depuis le premier conteneur du groupe (même s’il n’est pas running)
                ContainerSummary first = grp.First();
                Dictionary<string, string> labels = first.Labels ?? new Dictionary<string, string>();

                SlimFaasConfiguration config = ParseOrDefault(labels, Configuration, () => new SlimFaasConfiguration(),
                    json => JsonSerializer.Deserialize(json,
                                SlimFaasConfigurationSerializerContext.Default.SlimFaasConfiguration) ??
                            new SlimFaasConfiguration());

                int replicas = running.Count; // 👈 uniquement RUNNING
                bool endpointReady = pods.Any(p => (p.Ports?.Count ?? 0) > 0);

                DeploymentInformation di = new(
                    deploymentName,
                    kubeNamespace,
                    pods,
                    config,
                    replicas,
                    GetInt(labels, ReplicasAtStart, 1),
                    GetInt(labels, ReplicasMin, 0),
                    GetInt(labels, TimeoutSecondBeforeSetReplicasMin, 300),
                    GetInt(labels, NumberParallelRequest, 10),
                    GetBool(labels, ReplicasStartAsSoonAsOneFunctionRetrieveARequest, false),
                    PodType.Deployment,
                    SplitCsv(labels, DependsOn),
                    ParseOrDefault(labels, Schedule, () => new ScheduleConfig(),
                        json =>
                            JsonSerializer.Deserialize(json, ScheduleConfigSerializerContext.Default.ScheduleConfig) ??
                            new ScheduleConfig()),
                    GetSubscribeEvents(labels),
                    ParseEnum(labels, DefaultVisibility, FunctionVisibility.Public),
                    GetPathsStartWithVisibility(labels),
                    $"{replicas}-{pods.Count}-{endpointReady}",
                    endpointReady,
                    ParseEnum(labels, DefaultTrust, FunctionTrust.Trusted)
                );

                deployments.Add(di);
            }

            // AVANT le return, après avoir rempli deployments + allPods :
            IList<PodInformation> slimFaasPods = await GetSlimFaasSelfPodsAsync();
            SlimFaasDeploymentInformation slimfaasInfo = new(slimFaasPods.Count, slimFaasPods);

            // (facultatif) on ajoute aussi ces pods à la vue globale
            allPods.AddRange(slimFaasPods);

            // puis :
            return new DeploymentsInformations(deployments, slimfaasInfo, allPods);
        }

        public async Task CreateJobAsync(string kubeNamespace, string name, CreateJob createJob,
            string elementId, string jobFullName, long inQueueTimestamp)
        {
            Dictionary<string, string> labels = new()
            {
                [SlimfaasJobName] = jobFullName,
                [SlimfaasJobElementId] = elementId,
                [SlimfaasInQueueTimestamp] = inQueueTimestamp.ToString(CultureInfo.InvariantCulture),
                [SlimfaasJobStartTimestamp] = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
                [Function] = "false",
                [NamespaceLabel] = kubeNamespace
            };

            if (!string.IsNullOrWhiteSpace(_composeProject))
            {
                labels["com.docker.compose.project"] = _composeProject;
                labels["com.docker.compose.service"] = "slimfaas"; // ou "jobs" si tu veux une sous-ligne dédiée
                // labels["com.docker.compose.version"] = "2.0"; // optionnel
            }

            // ...
            int ttl = createJob.TtlSecondsAfterFinished; // 0 = pas de TTL
            if (ttl > 0)
            {
                labels[TtlSecondsAfterFinished] = ttl.ToString(CultureInfo.InvariantCulture);
            }

            await EnsureImagePresentAsync(createJob.Image);
            if (createJob.DependsOn is { Count: > 0 })
            {
                labels[DependsOn] = string.Join(",", createJob.DependsOn);
            }

            string[] env = (createJob.Environments ?? Array.Empty<EnvVarInput>())
                .Where(e => e.SecretRef == null && e.ConfigMapRef == null && e.FieldRef == null &&
                            e.ResourceFieldRef == null)
                .Select(e => $"{e.Name}={e.Value}")
                .ToArray();

            CreateContainer_HostConfig hostConfig = new()
            {
                // ⚠️ set false while you debug; flip back to true once stable
                AutoRemove = false
            };
            if (createJob.Resources?.Limits is not null)
            {
                if (createJob.Resources.Limits.TryGetValue("memory", out string? mem) &&
                    TryParseMemoryBytes(mem, out long bytes))
                {
                    hostConfig.Memory = bytes;
                }

                if (createJob.Resources.Limits.TryGetValue("cpu", out string? cpu))
                {
                    ApplyCpuLimit(hostConfig, cpu);
                }
            }

            await TryPullImageAsync(createJob.Image);

            // Attach to SlimFaas/compose network if we know it
            Dictionary<string, CreateContainer_EndpointSettings>? endpoints = null;
            if (!string.IsNullOrWhiteSpace(_networkName))
            {
                endpoints = new Dictionary<string, CreateContainer_EndpointSettings>
                {
                    [_networkName!] = new() { Aliases = new List<string> { jobFullName } }
                };
            }

            CreateContainerRequest createBody = new()
            {
                Image = createJob.Image,
                Name = jobFullName,
                Labels = labels,
                Env = env,
                Cmd = createJob.Args?.ToArray() ?? Array.Empty<string>(),
                HostConfig = hostConfig,
                NetworkingConfig = endpoints is null
                    ? null
                    : new CreateContainer_NetworkingConfig { EndpointsConfig = endpoints }
                // If you need to force an entrypoint, add: Entrypoint = new[] { "bash", "-lc" }
            };

            CreateContainerResponse? createRes = await PostJsonAsync<CreateContainerRequest, CreateContainerResponse>(
                $"{_apiPrefix}/containers/create?name={Uri.EscapeDataString(jobFullName)}",
                createBody,
                DockerJson.Default.CreateContainerRequest,
                DockerJson.Default.CreateContainerResponse);

            string id = createRes?.Id ??
                        throw new InvalidOperationException("Docker returned no container id for job creation.");
            _logger.LogInformation("Job create: name={Name} id={Id}", jobFullName, id);
            try
            {
                await PostAsync($"{_apiPrefix}/containers/{id}/start", null);
                await Task.Delay(150);
                InspectContainerResponse? insp = await TryInspectContainerAsync(id);
                _logger.LogInformation("Job started: {Name} state={State} exit={Exit} error={Err}",
                    jobFullName, insp?.State?.Running, insp?.State?.ExitCode, insp?.State?.Error);
            }
            catch (HttpRequestException ex)
            {
                // Optional: grab last logs to understand failure
                string tail = await GetContainerLogsAsync(id);
                _logger.LogError(ex, "Failed to start job {Job}. Logs tail:\n{Logs}", jobFullName, tail);
                throw;
            }
        }


        public async Task<IList<Job>> ListJobsAsync(string kubeNamespace)
        {
            FiltersLabelArray filters = new(new List<string>
            {
                SlimfaasJobName, // presence
                $"{NamespaceLabel}={kubeNamespace}"
            });
            string filterJson = JsonSerializer.Serialize(filters, DockerJson.Default.FiltersLabelArray);
            string url = $"{_apiPrefix}/containers/json?all=1&filters={WebUtility.UrlEncode(filterJson)}";

            List<ContainerSummary> containers = await GetAsync(url, DockerJson.Default.ListContainerSummary) ??
                                                new List<ContainerSummary>();

            List<Job> result = new();
            foreach (ContainerSummary c in containers)
            {
                InspectContainerResponse? insp = await TryInspectContainerAsync(c.ID);
                if (insp is null)
                {
                    continue;
                }

                List<string> ips = new();
                string ip = ExtractPreferredIPAddress(insp);
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    ips.Add(ip!);
                }

                JobStatus status = JobStatus.Pending;
                if (insp.State?.Running == true)
                {
                    status = JobStatus.Running;
                }
                else if (insp.State?.Running == false && insp.State?.ExitCode == 0)
                {
                    status = JobStatus.Succeeded;
                }
                else if (insp.State?.Running == false && insp.State?.ExitCode != 0)
                {
                    status = JobStatus.Failed;
                }

                if (!string.IsNullOrWhiteSpace(insp.State?.Error) &&
                    insp.State!.Error!.IndexOf("pull", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    status = JobStatus.ImagePullBackOff;
                }

                Dictionary<string, string> labels = new(StringComparer.OrdinalIgnoreCase);
                if (c.Labels is not null)
                {
                    foreach (KeyValuePair<string, string> kv in c.Labels)
                    {
                        labels[kv.Key] = kv.Value;
                    }
                }

                if (insp.Config?.Labels is not null)
                {
                    foreach (KeyValuePair<string, string> kv in insp.Config.Labels)
                    {
                        labels[kv.Key] = kv.Value;
                    }
                }

                IList<string> dependsOn = SplitCsv(labels, DependsOn);
                labels.TryGetValue(SlimfaasJobElementId, out string? elementId);
                long.TryParse(labels.GetValueOrDefault(SlimfaasInQueueTimestamp), out long inQueue);
                long.TryParse(labels.GetValueOrDefault(SlimfaasJobStartTimestamp), out long startTs);


                int ttlSec = 0;
                if (labels.TryGetValue(TtlSecondsAfterFinished, out string? ttlStr))
                {
                    int.TryParse(ttlStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ttlSec);
                }

                bool isFinished = status is JobStatus.Succeeded or JobStatus.Failed;
                if (isFinished && ttlSec > 0 && insp.State?.FinishedAt is DateTimeOffset fin)
                {
                    int ageSec = (int)(DateTimeOffset.UtcNow - fin.ToUniversalTime()).TotalSeconds;
                    if (ageSec >= ttlSec)
                    {
                        // 1) Ne pas l’afficher
                        // 2) Optionnel : le supprimer physiquement
                        try
                        {
                            await RemoveContainerAsync(insp.Id,
                                true); // stop if needed inside remove or call StopContainerIfRunningAsync
                        }
                        catch
                        {
                            /* ignore best effort */
                        }

                        continue; // 👈 saute l'ajout à la liste
                    }
                }

                result.Add(new Job(
                    TrimSlash(insp.Name) ?? c.Names?.FirstOrDefault()?.Trim('/') ?? c.ID[..12],
                    status,
                    ips,
                    dependsOn,
                    elementId ?? "",
                    inQueue,
                    startTs
                ));
            }

            return result;
        }

        public async Task DeleteJobAsync(string kubeNamespace, string jobName)
        {
            string? id = await ResolveContainerIdByNameAsync(jobName);
            if (id is null)
            {
                _logger.LogWarning("DeleteJob: container '{JobName}' not found.", jobName);
                return;
            }

            await StopContainerIfRunningAsync(id);
            await RemoveContainerAsync(id, true);
        }

        private async Task<string?> GetComposeProjectAsync()
        {
            try
            {
                string selfId = Dns.GetHostName();
                InspectContainerResponse? insp = await TryInspectContainerAsync(selfId);
                return insp?.Config?.Labels?.GetValueOrDefault("com.docker.compose.project");
            }
            catch
            {
                return null;
            }
        }

        private async Task<string?> GetSelfPrimaryNetworkNameAsync()
        {
            try
            {
                string selfId = Dns.GetHostName(); // ex: a1b2c3…
                InspectContainerResponse?
                    insp = await TryInspectContainerAsync(selfId); // utilise ta TryInspectContainerAsync
                Dictionary<string, Inspect_EndpointSettings>? nets = insp?.NetworkSettings?.Networks;
                if (nets is null || nets.Count == 0)
                {
                    return null;
                }

                // Prend un réseau utilisateur (≠ bridge/host/none) si possible
                foreach (string key in nets.Keys)
                {
                    if (!string.Equals(key, "bridge", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(key, "host", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        return key;
                    }
                }

                // sinon le premier dispo
                return nets.Keys.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // ------------------ IKubernetesService ------------------

        private async Task EnsureTemplateContainerAsync(string deployment, string ns, InspectContainerResponse source)
        {
            // 1) Existe déjà ?
            List<ContainerSummary> existing = await ListContainersByLabelsAsync(
                new Dictionary<string, string?>
                {
                    [Function] = null,
                    [AppLabel] = deployment,
                    [NamespaceLabel] = ns,
                    [TemplateLabel] = "true"
                }, true);
            existing = existing.Where(c => c.Labels is not null
                                           && c.Labels.TryGetValue(Function, out var v)
                                           && IsTrueLike(v)).ToList();
            if (existing.Any())
            {
                return; // déjà un template
            }

            // 2) Labels de base + flag template
            Dictionary<string, string> labels = new(source.Config?.Labels ?? new Dictionary<string, string>())
            {
                [Function] = FunctionTrue, [AppLabel] = deployment, [NamespaceLabel] = ns, [TemplateLabel] = "true"
            };

            // 3) Crée un conteneur ARRÊTÉ (AutoRemove=false), non démarré
            string name = $"{deployment}-template";
            // éviter collision de nom
            int i = 1;
            while (await ResolveContainerIdByNameAsync(name) is not null)
            {
                name = $"{deployment}-template-{i++}";
            }

            string imageRef = source.Config?.Image ?? source.Image;
            InspectImageResponse? img = await InspectImageAsync(imageRef);
            Dictionary<string, CreateContainer_EndpointSettings>? endpoints = _networkName is not null
                ? new Dictionary<string, CreateContainer_EndpointSettings>
                {
                    [_networkName] = new()
                    {
                        Aliases = new List<string> { deployment, name } // DNS utiles
                    }
                }
                : null;

            // publis d'origine du replica source (s'il y en a)
            Dictionary<string, List<CreateContainer_PortBinding>> publishedMap =
                BuildPublishedBindingsFromInspect(source);
            if (publishedMap.Count > 0)
            {
                labels[PublishedPortsLabel] = SerializePublishedBindingsLabel(publishedMap);
            }

            CreateContainerRequest createBody = new()
            {
                Image = imageRef,
                Name = name,
                Labels = labels,
                Env = BuildEnvForClone(source, img),
                Cmd = source.Config?.Cmd ?? Array.Empty<string>(),
                HostConfig = new CreateContainer_HostConfig { AutoRemove = false },
                ExposedPorts = BuildExposedPortsForClone(source, img), // 👈 ports corrects
                NetworkingConfig = endpoints is null
                    ? null
                    : new CreateContainer_NetworkingConfig { EndpointsConfig = endpoints }
            };

            await PostJsonAsync<CreateContainerRequest, CreateContainerResponse>(
                $"{_apiPrefix}/containers/create?name={Uri.EscapeDataString(name)}",
                createBody,
                DockerJson.Default.CreateContainerRequest,
                DockerJson.Default.CreateContainerResponse);

            // Ne PAS démarrer : on veut juste une “carcasse” pour porter les métadonnées
        }


        private string ExtractPreferredIPAddress(InspectContainerResponse insp)
        {
            // 1) IP sur le réseau préféré
            if (_networkName is not null &&
                insp.NetworkSettings?.Networks is { } nets &&
                nets.TryGetValue(_networkName, out Inspect_EndpointSettings? ep) &&
                !string.IsNullOrWhiteSpace(ep.IPAddress))
            {
                return ep.IPAddress!;
            }

            // 2) Première IP d’un réseau attaché
            if (insp.NetworkSettings?.Networks is { } nets2)
            {
                foreach (KeyValuePair<string, Inspect_EndpointSettings> kv in nets2)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value.IPAddress))
                    {
                        return kv.Value.IPAddress!;
                    }
                }
            }

            // 3) IP “legacy”
            return insp.NetworkSettings?.IPAddress ?? "";
        }

        private static List<int> ExtractPrivatePorts(InspectContainerResponse insp)
        {
            List<int> ports = new();
            if (insp.NetworkSettings?.Ports != null)
            {
                foreach (KeyValuePair<string, List<Inspect_PortBinding>?> kv in insp.NetworkSettings.Ports)
                {
                    // key example: "5000/tcp"
                    string key = kv.Key;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    int idx = key.IndexOf('/');
                    if (idx <= 0)
                    {
                        continue;
                    }

                    if (int.TryParse(key.AsSpan(0, idx), out int p))
                    {
                        ports.Add(p);
                    }
                }
            }

            return ports.Distinct().ToList();
        }

        private async Task EnsureImagePresentAsync(string image)
        {
            if (string.IsNullOrWhiteSpace(image))
            {
                throw new ArgumentException("Image is empty.", nameof(image));
            }

            // 1) Try local inspect first
            InspectImageResponse? exists = await InspectImageAsync(image);
            if (exists is not null)
            {
                return;
            }

            // 2) Pull; if it fails, throw with body for visibility
            string imageName = image;
            string tag = "latest";
            int idx = image.LastIndexOf(':');
            if (idx > 0 && idx < image.Length - 1 && !image.Contains('@'))
            {
                imageName = image[..idx];
                tag = image[(idx + 1)..];
            }

            string path =
                $"{_apiPrefix}/images/create?fromImage={Uri.EscapeDataString(imageName)}&tag={Uri.EscapeDataString(tag)}";
            using HttpRequestMessage req =
                new(HttpMethod.Post, path) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            using HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            if (!res.IsSuccessStatusCode)
            {
                string body = await res.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Image pull failed for '{image}': {(int)res.StatusCode} {res.ReasonPhrase}\n{body}");
            }

            // 3) Re‑inspect to be sure
            InspectImageResponse? check = await InspectImageAsync(image);
            if (check is null)
            {
                throw new InvalidOperationException($"Image '{image}' not present after pull.");
            }
        }

        private async Task<string> GetContainerLogsAsync(string id, int tail = 200)
        {
            string url = $"{_apiPrefix}/containers/{id}/logs?stdout=1&stderr=1&tail={tail}";
            using HttpResponseMessage res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync();
        }

        // ------------------ Internal helpers ------------------

        private async Task CreateAndStartReplicaFromTemplateAsync(string deployment, string ns,
    InspectContainerResponse template)
{
    var labels = new Dictionary<string, string>(template.Config?.Labels ?? new())
    {
        [Function] = FunctionTrue,
        [AppLabel] = deployment,
        [NamespaceLabel] = ns
    };

    string name = $"{deployment}-{Guid.NewGuid():N}".Substring(0, 20);
    string imageRef = template.Config?.Image ?? template.Image;
    var img = await InspectImageAsync(imageRef);

    Dictionary<string, CreateContainer_EndpointSettings>? endpoints =
        _networkName is not null
            ? new() { [_networkName] = new() { Aliases = new List<string> { deployment, name } } }
            : null;

    var createBody = new CreateContainerRequest
    {
        Image = imageRef,
        Name = name,
        Labels = labels,
        Env = BuildEnvForClone(template, img),
        Cmd = template.Config?.Cmd ?? Array.Empty<string>(),
        HostConfig = new CreateContainer_HostConfig
        {
            AutoRemove = true,
            // ⚠️ IMPORTANT: ne pas publier par défaut → laisse null
            PortBindings = null
        },
        ExposedPorts = BuildExposedPortsForClone(template, img),
        NetworkingConfig = endpoints is null ? null : new CreateContainer_NetworkingConfig { EndpointsConfig = endpoints }
    };

    // Si le template porte des bindings persistés, on peut choisir de les respecter,
    // mais pour éviter les collisions, on LES RANDOMISE pour les replicas.
    var persisted = TryParsePublishedBindingsLabel(template.Config?.Labels);
    if (persisted is not null && persisted.Count > 0)
    {
        createBody.HostConfig ??= new CreateContainer_HostConfig { AutoRemove = true };
        createBody.HostConfig.PortBindings = persisted;
        // randomise tous les HostPort pour éviter collision
        MakeBindingsRandomHostPort(createBody);
    }
    // SINON: ne rien publier (PortBindings=null) → accès via réseau interne

    // Création
    CreateContainerResponse? createRes;
    try
    {
        createRes = await PostJsonAsync<CreateContainerRequest, CreateContainerResponse>(
            $"{_apiPrefix}/containers/create?name={Uri.EscapeDataString(name)}",
            createBody,
            DockerJson.Default.CreateContainerRequest,
            DockerJson.Default.CreateContainerResponse);
    }
    catch (HttpRequestException)
    {
        // Dernière cartouche: si quelqu’un a mis des bindings avant nous, on randomise et on retente
        MakeBindingsRandomHostPort(createBody);
        createRes = await PostJsonAsync<CreateContainerRequest, CreateContainerResponse>(
            $"{_apiPrefix}/containers/create?name={Uri.EscapeDataString(name)}",
            createBody,
            DockerJson.Default.CreateContainerRequest,
            DockerJson.Default.CreateContainerResponse);
    }

    string id = createRes?.Id ?? throw new InvalidOperationException("Create container returned no Id.");

    try
    {
        await PostAsync($"{_apiPrefix}/containers/{id}/start", null);
    }
    catch (HttpRequestException ex)
    {
        // Diagnostic utile
        string logs = await GetContainerLogsAsync(id);
        _logger.LogWarning(ex, "Start failed for replica {Name} (id={Id}). Logs tail:\n{Logs}", name, id, logs);

        // Port collision typique: retente une fois en randomisant les bindings
        if (createBody.HostConfig?.PortBindings is not null)
        {
            try
            {
                // Stop + remove le raté
                await StopContainerIfRunningAsync(id);
                await RemoveContainerAsync(id, true);
            }
            catch { /* best effort */ }

            MakeBindingsRandomHostPort(createBody);
            var retry = await PostJsonAsync<CreateContainerRequest, CreateContainerResponse>(
                $"{_apiPrefix}/containers/create?name={Uri.EscapeDataString(name)}",
                createBody,
                DockerJson.Default.CreateContainerRequest,
                DockerJson.Default.CreateContainerResponse);

            string id2 = retry?.Id ?? throw new InvalidOperationException("Create container retry returned no Id.");
            await PostAsync($"{_apiPrefix}/containers/{id2}/start", null);
        }
        else
        {
            // Rien n’était publié → rééchec ≠ port collision → remonter l’erreur
            throw;
        }
    }
}


        // Construit PortBindings à partir des ExposedPorts (clés "5000/tcp")
// preferSameHostPort=true => tente 5000:5000 ; si on retente après échec on passera en host-port aléatoire.
        private static void AttachPublishBindingsFromExposedPorts(
            CreateContainerRequest req,
            bool preferSameHostPort)
        {
            if (req.ExposedPorts is null || req.ExposedPorts.Count == 0)
            {
                return;
            }

            req.HostConfig ??= new CreateContainer_HostConfig();
            Dictionary<string, List<CreateContainer_PortBinding>> bindings = new(StringComparer.Ordinal);

            foreach (string key in req.ExposedPorts.Keys) // ex: "5000/tcp"
            {
                string port = key.Split('/')[0]; // "5000"
                bindings[key] = new List<CreateContainer_PortBinding>
                {
                    new()
                    {
                        HostIp = "0.0.0.0",
                        HostPort = preferSameHostPort ? port : null // null => port aléatoire choisi par Docker
                    }
                };
            }

            req.HostConfig.PortBindings = bindings;
        }

// Si la création échoue (port occupé), on bascule tous les bindings en HostPort aléatoire
        private static void MakeBindingsRandomHostPort(CreateContainerRequest req)
        {
            if (req.HostConfig?.PortBindings is null)
            {
                return;
            }

            foreach (List<CreateContainer_PortBinding> list in req.HostConfig.PortBindings.Values)
            foreach (CreateContainer_PortBinding b in list)
            {
                b.HostPort = null;
            }
        }


        private async Task<IList<PodInformation>> GetSlimFaasSelfPodsAsync()
        {
            List<PodInformation> pods = new();

            // 1) Essayer via le vrai hostname Linux (short container ID)
            try
            {
                string selfKey = Dns.GetHostName(); // ex: "a1b2c3d4e5f6"
                InspectContainerResponse insp = await InspectContainerAsync(selfKey); // id prefix OK
                pods.Add(ToPod(insp, "slimfaas"));
                return pods;
            }
            catch
            {
                // ignore → fallback
            }

            // 2) Fallback: conteneurs RUNNING du service compose "slimfaas"
            Dictionary<string, bool> labelDict = new() { ["com.docker.compose.service=slimfaas"] = true };
            FiltersLabel filter = new(labelDict);
            string json = JsonSerializer.Serialize(filter, DockerJson.Default.FiltersLabel);
            string url =
                $"{_apiPrefix}/containers/json?all=0&filters={WebUtility.UrlEncode(json)}"; // all=0 => running only

            List<ContainerSummary> containers = await GetAsync(url, DockerJson.Default.ListContainerSummary) ??
                                                new List<ContainerSummary>();
            foreach (ContainerSummary c in containers)
            {
                InspectContainerResponse insp = await InspectContainerAsync(c.ID);
                pods.Add(ToPod(insp, "slimfaas"));
            }

            return pods;
        }

        private PodInformation ToPod(InspectContainerResponse insp, string deploymentName) =>
            new(
                TrimSlash(insp.Name) ?? insp.Id[..12],
                insp.State?.StartedAt is not null,
                insp.State?.Running == true &&
                (insp.State.Health == null || string.Equals(insp.State.Health.Status, "healthy",
                    StringComparison.OrdinalIgnoreCase)),
                ExtractPreferredIPAddress(insp),
                deploymentName,
                GetAllContainerPortsNoHeuristic(insp),
                insp.Created?.ToUniversalTime().Ticks.ToString() ?? DateTime.UtcNow.Ticks.ToString()
            );


        private async Task<List<ContainerSummary>> ListContainersByLabelsAsync(
            Dictionary<string, string?> labels, bool all)
        {
            var list = new List<string>(labels.Count);
            foreach (var kv in labels)
            {
                // si valeur null => filtre "présence du label"
                list.Add(kv.Value is null ? kv.Key : $"{kv.Key}={kv.Value}");
            }

            var filters = new FiltersLabelArray(list);
            string filterJson = JsonSerializer.Serialize(filters, DockerJson.Default.FiltersLabelArray);
            string url = $"{_apiPrefix}/containers/json?all={(all ? 1 : 0)}&filters={WebUtility.UrlEncode(filterJson)}";

            return await GetAsync(url, DockerJson.Default.ListContainerSummary) ?? new List<ContainerSummary>();
        }



        private async Task<InspectContainerResponse> InspectContainerAsync(string id)
        {
            InspectContainerResponse resp =
                await GetAsync($"{_apiPrefix}/containers/{id}/json", DockerJson.Default.InspectContainerResponse)
                ?? throw new InvalidOperationException($"Cannot inspect container {id}");
            return resp;
        }

        private Task<InspectContainerResponse?> TryInspectContainerAsync(string id) =>
            // GetAsync(...) renvoie null si status != 2xx → pas d’exception
            GetAsync($"{_apiPrefix}/containers/{id}/json", DockerJson.Default.InspectContainerResponse);

        private async Task StopContainerIfRunningAsync(string id)
        {
            try
            {
                await PostAsync($"{_apiPrefix}/containers/{id}/stop", null);
            }
            catch (HttpRequestException)
            {
                /* ignore */
            }
        }

        private async Task RemoveContainerAsync(string id, bool force) =>
            await DeleteAsync($"{_apiPrefix}/containers/{id}?force={(force ? "true" : "false")}");

        private async Task<string?> ResolveContainerIdByNameAsync(string name)
        {
            FiltersName filter = new(new Dictionary<string, bool> { [name] = true });
            string filterJson = JsonSerializer.Serialize(filter, DockerJson.Default.FiltersName);
            string url = $"{_apiPrefix}/containers/json?all=1&filters={WebUtility.UrlEncode(filterJson)}";

            List<ContainerSummary>? list = await GetAsync(url, DockerJson.Default.ListContainerSummary);
            return list?.FirstOrDefault()?.ID;
        }

        private async Task TryPullImageAsync(string image)
        {
            if (string.IsNullOrWhiteSpace(image))
            {
                return;
            }

            string imageName = image;
            string tag = "latest";
            int idx = image.LastIndexOf(':');
            if (idx > 0 && idx < image.Length - 1 && !image.Contains('@'))
            {
                imageName = image[..idx];
                tag = image[(idx + 1)..];
            }

            try
            {
                string path =
                    $"{_apiPrefix}/images/create?fromImage={Uri.EscapeDataString(imageName)}&tag={Uri.EscapeDataString(tag)}";
                using HttpRequestMessage req = new(HttpMethod.Post, path);
                req.Content = new ByteArrayContent(Array.Empty<byte>());
                using HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
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
            => labels.TryGetValue(key, out string? v) && int.TryParse(v, out int i) ? i : defVal;

        private static bool GetBool(IReadOnlyDictionary<string, string> labels, string key, bool defVal)
            => labels.TryGetValue(key, out string? v) && bool.TryParse(v, out bool b) ? b : defVal;

        private static IList<string> SplitCsv(IReadOnlyDictionary<string, string> labels, string key)
        {
            if (!labels.TryGetValue(key, out string? v) || string.IsNullOrWhiteSpace(v))
            {
                return Array.Empty<string>();
            }

            return v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static TEnum ParseEnum<TEnum>(IReadOnlyDictionary<string, string> labels, string key, TEnum defVal)
            where TEnum : struct
        {
            if (labels.TryGetValue(key, out string? v) && Enum.TryParse<TEnum>(v, true, out TEnum parsed))
            {
                return parsed;
            }

            return defVal;
        }

        private static T ParseOrDefault<T>(IReadOnlyDictionary<string, string> labels, string key, Func<T> defFactory,
            Func<string, T> parser)
        {
            if (labels.TryGetValue(key, out string? json) && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    return parser(json);
                }
                catch
                {
                    /* ignore parse issue */
                }
            }

            return defFactory();
        }

        private static IList<PathVisibility> GetPathsStartWithVisibility(IReadOnlyDictionary<string, string> labels)
        {
            if (!labels.TryGetValue(PathsStartWithVisibility, out string? raw) || string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<PathVisibility>();
            }

            string[] tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<PathVisibility> list = new(tokens.Length);

            foreach (string token in tokens)
            {
                string[] parts = token.Split(':', 2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                FunctionVisibility vis = FunctionVisibility.Public;
                string path;

                if (parts.Length == 2)
                {
                    if (parts[0].Equals("Private", StringComparison.OrdinalIgnoreCase))
                    {
                        vis = FunctionVisibility.Private;
                    }
                    else if (parts[0].Equals("Public", StringComparison.OrdinalIgnoreCase))
                    {
                        vis = FunctionVisibility.Public;
                    }

                    path = parts[1];
                }
                else
                {
                    path = parts[0];
                }

                list.Add(new PathVisibility(path, vis));
            }

            return list;
        }

        private static IList<SubscribeEvent> GetSubscribeEvents(IReadOnlyDictionary<string, string> labels)
        {
            if (!labels.TryGetValue(SubscribeEvents, out string? raw) || string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<SubscribeEvent>();
            }

            string[] tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<SubscribeEvent> list = new(tokens.Length);

            foreach (string token in tokens)
            {
                string[] parts = token.Split(':', 2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                FunctionVisibility vis = FunctionVisibility.Public;
                string name;

                if (parts.Length == 2)
                {
                    if (parts[0].Equals("Private", StringComparison.OrdinalIgnoreCase))
                    {
                        vis = FunctionVisibility.Private;
                    }
                    else if (parts[0].Equals("Public", StringComparison.OrdinalIgnoreCase))
                    {
                        vis = FunctionVisibility.Public;
                    }

                    name = parts[1];
                }
                else
                {
                    name = parts[0];
                }

                list.Add(new SubscribeEvent(name, vis));
            }

            return list;
        }

        private static void ApplyCpuLimit(CreateContainer_HostConfig hc, string cpu)
        {
            // Accept "500m", "0.5", "1", "2"
            if (cpu.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                string num = cpu[..^1];
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double milli))
                {
                    hc.CPUPeriod = 100_000; // 100ms
                    hc.CPUQuota = (long)(milli / 1000.0 * 100_000.0);
                }

                return;
            }

            if (double.TryParse(cpu, NumberStyles.Float, CultureInfo.InvariantCulture, out double cores))
            {
                hc.CPUPeriod = 100_000;
                hc.CPUQuota = (long)(cores * 100_000.0);
            }
        }

        private static bool TryParseMemoryBytes(string value, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            value = value.Trim();
            long mul = 1;

            if (value.EndsWith("Ki", StringComparison.OrdinalIgnoreCase))
            {
                mul = 1024;
                value = value[..^2];
            }
            else if (value.EndsWith("Mi", StringComparison.OrdinalIgnoreCase))
            {
                mul = 1024L * 1024;
                value = value[..^2];
            }
            else if (value.EndsWith("Gi", StringComparison.OrdinalIgnoreCase))
            {
                mul = 1024L * 1024 * 1024;
                value = value[..^2];
            }
            else if (value.EndsWith("Ti", StringComparison.OrdinalIgnoreCase))
            {
                mul = 1024L * 1024 * 1024 * 1024;
                value = value[..^2];
            }
            else if (value.EndsWith("K", StringComparison.OrdinalIgnoreCase))
            {
                mul = 1000;
                value = value[..^1];
            }
            else if (value.EndsWith("M", StringComparison.OrdinalIgnoreCase))
            {
                mul = 1000L * 1000;
                value = value[..^1];
            }
            else if (value.EndsWith("G", StringComparison.OrdinalIgnoreCase))
            {
                mul = 1000L * 1000 * 1000;
                value = value[..^1];
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                return false;
            }

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
                string path = candidate.Replace("unix://", "", StringComparison.OrdinalIgnoreCase);
                SocketsHttpHandler handler = new()
                {
                    ConnectCallback = async (ctx, ct) =>
                    {
                        UnixDomainSocketEndPoint ep = new(path);
                        Socket sock = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
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

                        return new NetworkStream(sock, true);
                    }
                };
                baseAddress = new Uri("http://localhost"); // hôte bidon requis par HttpClient
                return new HttpClient(handler);
            }

            // TCP (http/https)
            if (!candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "http://" + candidate.Trim();
            }

            Uri uri = new(candidate);

            // 5) Si l’URL est http(s) sans port explicite, on met un port par défaut Docker Desktop
            if (uri.Scheme == Uri.UriSchemeHttp && (uri.IsDefaultPort || uri.Port == 80))
            {
                uri = new UriBuilder(uri) { Port = 2375 }.Uri;
            }
            else if (uri.Scheme == Uri.UriSchemeHttps && (uri.IsDefaultPort || uri.Port == 443))
            {
                uri = new UriBuilder(uri) { Port = 2376 }.Uri;
            }

            baseAddress = uri;

            HttpClient http = new(new SocketsHttpHandler
            {
                // En dev, tolère un TLS autosigné. ⚠️ À durcir en prod.
                SslOptions = new SslClientAuthenticationOptions
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
                DockerVersionResponse? ver =
                    await http.GetFromJsonAsync("/version", DockerJson.Default.DockerVersionResponse);
                if (!string.IsNullOrWhiteSpace(ver?.ApiVersion))
                {
                    return $"/v{ver!.ApiVersion}";
                }
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
            using HttpResponseMessage res = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode)
            {
                return default;
            }

            await using Stream s = await res.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync(s, typeInfo);
        }

        private async Task<TRes?> PostJsonAsync<TReq, TRes>(
            string path,
            TReq body,
            JsonTypeInfo<TReq> reqTypeInfo,
            JsonTypeInfo<TRes> resTypeInfo)
        {
            string json = JsonSerializer.Serialize(body, reqTypeInfo);
            using HttpResponseMessage res =
                await _http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
            res.EnsureSuccessStatusCode();
            await using Stream s = await res.Content.ReadAsStreamAsync();
            if (s.CanSeek && s.Length == 0)
            {
                return default;
            }

            return await JsonSerializer.DeserializeAsync(s, resTypeInfo);
        }

        private async Task PostAsync(string path, HttpContent? body)
        {
            using HttpResponseMessage res = await _http.PostAsync(path, body);
            if (!res.IsSuccessStatusCode)
            {
                string err = await res.Content.ReadAsStringAsync();
                throw new HttpRequestException($"POST {path} failed: {(int)res.StatusCode} {res.ReasonPhrase}\n{err}");
            }
        }

        private async Task DeleteAsync(string path)
        {
            using HttpResponseMessage res = await _http.DeleteAsync(path);
            if (!res.IsSuccessStatusCode)
            {
                string err = await res.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"DELETE {path} failed: {(int)res.StatusCode} {res.ReasonPhrase}\n{err}");
            }
        }

        private Task<InspectImageResponse?> InspectImageAsync(string imageRef)
        {
            if (string.IsNullOrWhiteSpace(imageRef))
            {
                return Task.FromResult<InspectImageResponse?>(null);
            }

            // imageRef peut être "repo/name:tag" ou un digest/id
            string url = $"{_apiPrefix}/images/{Uri.EscapeDataString(imageRef)}/json";
            return GetAsync(url, DockerJson.Default.InspectImageResponse);
        }

        private static Dictionary<string, Dictionary<string, string>> BuildExposedPortsForClone(
            InspectContainerResponse template,
            InspectImageResponse? image)
        {
            static Dictionary<string, string> Empty()
            {
                return new Dictionary<string, string>();
            }

            Dictionary<string, Dictionary<string, string>> exposed = new(StringComparer.Ordinal);

            // 1) Conteneur: Config.ExposedPorts
            if (template.Config?.ExposedPorts is { Count: > 0 })
            {
                foreach (string k in template.Config.ExposedPorts.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        exposed[k] = Empty();
                    }
                }
            }

            // 2) Image: Config.ExposedPorts
            if (exposed.Count == 0 && image?.Config?.ExposedPorts is { Count: > 0 })
            {
                foreach (string k in image.Config.ExposedPorts.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        exposed[k] = Empty();
                    }
                }
            }

            // 3) Fallback: NetworkSettings.Ports ("5000/tcp")
            if (exposed.Count == 0 && template.NetworkSettings?.Ports is { Count: > 0 })
            {
                foreach (string k in template.NetworkSettings.Ports.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        exposed[k] = Empty();
                    }
                }
            }

            return exposed;
        }


        private static string[] BuildEnvForClone(
            InspectContainerResponse template,
            InspectImageResponse? image
        )
        {
            // 1) Conteneur
            if (template.Config?.Env is { Length: > 0 })
            {
                return template.Config.Env;
            }

            // 2) Image
            if (image?.Config?.Env is { Length: > 0 })
            {
                return image.Config.Env;
            }

            // 3) Rien
            return Array.Empty<string>();
        }

        private static List<int> GetAllContainerPortsNoHeuristic(InspectContainerResponse insp)
        {
            HashSet<int> set = new();

            // NetworkSettings.Ports
            if (insp.NetworkSettings?.Ports != null)
            {
                foreach (var key in insp.NetworkSettings.Ports.Keys)
                {
                    int? idx = key?.IndexOf('/');
                    if (idx > 0 && int.TryParse(key!.AsSpan(0, idx.Value), out int p))
                    {
                        set.Add(p);
                    }
                }
            }

            // Config.ExposedPorts
            if (insp.Config?.ExposedPorts != null)
            {
                foreach (var key in insp.Config.ExposedPorts.Keys)
                {
                    int? idx = key?.IndexOf('/');
                    if (idx > 0 && int.TryParse(key!.AsSpan(0, idx.Value), out int p))
                    {
                        set.Add(p);
                    }
                }
            }

            return set.OrderByDescending(n => n).ToList();
        }

// Extrait les PortBindings publiés (NetworkSettings.Ports) d'un inspect conteneur
        private static Dictionary<string, List<CreateContainer_PortBinding>> BuildPublishedBindingsFromInspect(
            InspectContainerResponse insp)
        {
            Dictionary<string, List<CreateContainer_PortBinding>> result = new(StringComparer.Ordinal);
            Dictionary<string, List<Inspect_PortBinding>?>? ports = insp.NetworkSettings?.Ports;
            if (ports is null || ports.Count == 0)
            {
                return result;
            }

            foreach ((string key, List<Inspect_PortBinding>? list) in ports)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (list is null || list.Count == 0)
                {
                    continue; // non publié
                }

                List<CreateContainer_PortBinding> mapped = new(list.Count);
                foreach (Inspect_PortBinding b in list)
                {
                    mapped.Add(new CreateContainer_PortBinding { HostIp = b.HostIp, HostPort = b.HostPort });
                }

                result[key] = mapped;
            }

            return result;
        }

// Sérialise en string pour le label
        private static string SerializePublishedBindingsLabel(Dictionary<string, List<CreateContainer_PortBinding>> map)
            => JsonSerializer.Serialize(map, DockerJson.Default.DictionaryStringListCreateContainer_PortBinding);

// Essaie de lire/parse le label
        private static Dictionary<string, List<CreateContainer_PortBinding>>? TryParsePublishedBindingsLabel(
            IReadOnlyDictionary<string, string>? labels)
        {
            if (labels is null)
            {
                return null;
            }

            if (!labels.TryGetValue(PublishedPortsLabel, out string? json) || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(json,
                    DockerJson.Default.DictionaryStringListCreateContainer_PortBinding);
            }
            catch
            {
                return null;
            }
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

    public record DockerVersionResponse(
        [property: JsonPropertyName("ApiVersion")]
        string ApiVersion,
        [property: JsonPropertyName("Version")]
        string Version
    );

    internal record FiltersLabelArray([property: JsonPropertyName("label")] List<string> Labels);

    public record ContainerSummary(
        [property: JsonPropertyName("Id")] string ID,
        [property: JsonPropertyName("Names")] List<string>? Names,
        [property: JsonPropertyName("Image")] string Image,
        [property: JsonPropertyName("State")] string State,
        [property: JsonPropertyName("Status")] string Status,
        [property: JsonPropertyName("Labels")] Dictionary<string, string>? Labels
    );

    public record InspectContainerResponse(
        [property: JsonPropertyName("Id")] string Id,
        [property: JsonPropertyName("Name")] string? Name,
        [property: JsonPropertyName("Image")] string Image,
        [property: JsonPropertyName("Created")]
        DateTimeOffset? Created,
        [property: JsonPropertyName("Config")] Inspect_Config? Config,
        [property: JsonPropertyName("State")] Inspect_State? State,
        [property: JsonPropertyName("NetworkSettings")]
        Inspect_NetworkSettings? NetworkSettings
    );

    public record Inspect_Config(
        [property: JsonPropertyName("Image")] string? Image,
        [property: JsonPropertyName("Env")] string[]? Env,
        [property: JsonPropertyName("Cmd")] string[]? Cmd,
        [property: JsonPropertyName("Labels")] Dictionary<string, string>? Labels,
        [property: JsonPropertyName("ExposedPorts")]
        Dictionary<string, object>? ExposedPorts
    );

    public record Inspect_State(
        [property: JsonPropertyName("Running")]
        bool Running,
        [property: JsonPropertyName("ExitCode")]
        int ExitCode,
        [property: JsonPropertyName("Error")] string? Error,
        [property: JsonPropertyName("Health")] Inspect_Health? Health,
        [property: JsonPropertyName("StartedAt")]
        DateTimeOffset? StartedAt,
        [property: JsonPropertyName("FinishedAt")]
        DateTimeOffset? FinishedAt
    );

    public record Inspect_Health([property: JsonPropertyName("Status")] string Status);

    public record Inspect_NetworkSettings(
        [property: JsonPropertyName("IPAddress")]
        string? IPAddress,
        [property: JsonPropertyName("Networks")]
        Dictionary<string, Inspect_EndpointSettings>? Networks,
        [property: JsonPropertyName("Ports")] Dictionary<string, List<Inspect_PortBinding>?>? Ports
    );

    public record Inspect_EndpointSettings(
        [property: JsonPropertyName("IPAddress")]
        string? IPAddress);

    public record Inspect_PortBinding(
        [property: JsonPropertyName("HostIp")] string? HostIp,
        [property: JsonPropertyName("HostPort")]
        string? HostPort
    );

    public record CreateContainerRequest
    {
        [JsonPropertyName("Image")] public string? Image { get; init; }
        [JsonPropertyName("Name")] public string? Name { get; init; }
        [JsonPropertyName("Labels")] public Dictionary<string, string>? Labels { get; init; }
        [JsonPropertyName("Env")] public string[]? Env { get; init; }
        [JsonPropertyName("Cmd")] public string[]? Cmd { get; init; }
        [JsonPropertyName("HostConfig")] public CreateContainer_HostConfig? HostConfig { get; set; }

        // AVANT: Dictionary<string, object>?  (⚠️ causait new {})
        // APRÈS: dictionnaire → dictionnaire vide
        [JsonPropertyName("ExposedPorts")]
        public Dictionary<string, Dictionary<string, string>>? ExposedPorts { get; init; }

        [JsonPropertyName("NetworkingConfig")] public CreateContainer_NetworkingConfig? NetworkingConfig { get; init; }
    }

    public record CreateContainer_HostConfig
    {
        [JsonPropertyName("AutoRemove")] public bool AutoRemove { get; set; }
        [JsonPropertyName("Memory")] public long? Memory { get; set; }
        [JsonPropertyName("CpuPeriod")] public long? CPUPeriod { get; set; }

        [JsonPropertyName("CpuQuota")] public long? CPUQuota { get; set; }

        // 👇 publication des ports
        [JsonPropertyName("PortBindings")]
        public Dictionary<string, List<CreateContainer_PortBinding>>? PortBindings { get; set; }
    }

    public record CreateContainerResponse([property: JsonPropertyName("Id")] string Id);

    public record InspectImageResponse(
        [property: JsonPropertyName("Id")] string Id,
        [property: JsonPropertyName("Config")] InspectImage_Config? Config,
        [property: JsonPropertyName("ContainerConfig")]
        InspectImage_Config? ContainerConfig
    );

    public record InspectImage_Config(
        [property: JsonPropertyName("Env")] string[]? Env,
        [property: JsonPropertyName("ExposedPorts")]
        Dictionary<string, object>? ExposedPorts
    );


    public record CreateContainer_NetworkingConfig
    {
        [JsonPropertyName("EndpointsConfig")]
        public Dictionary<string, CreateContainer_EndpointSettings>? EndpointsConfig { get; init; }
    }

    public record CreateContainer_EndpointSettings
    {
        [JsonPropertyName("Aliases")] public List<string>? Aliases { get; init; }
    }

    public record CreateContainer_PortBinding
    {
        [JsonPropertyName("HostIp")] public string? HostIp { get; init; }
        [JsonPropertyName("HostPort")] public string? HostPort { get; set; }
    }


    [JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(DockerVersionResponse))]
    [JsonSerializable(typeof(List<ContainerSummary>))]
    [JsonSerializable(typeof(InspectContainerResponse))]
    [JsonSerializable(typeof(CreateContainerRequest))]
    [JsonSerializable(typeof(CreateContainerResponse))]
    [JsonSerializable(typeof(FiltersLabel))]
    [JsonSerializable(typeof(FiltersName))]
    [JsonSerializable(typeof(InspectImageResponse))]
    [JsonSerializable(typeof(FiltersLabelArray))]
    [JsonSerializable(typeof(Dictionary<string, List<CreateContainer_PortBinding>>))]
    [JsonSerializable(typeof(object))]
    internal partial class DockerJson : JsonSerializerContext
    {
    }
}
