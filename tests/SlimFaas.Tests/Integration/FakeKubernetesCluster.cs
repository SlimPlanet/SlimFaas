using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using k8s;
using k8s.Models;

namespace SlimFaas.Tests.Integration;

/// <summary>
/// In-memory fake of the Kubernetes API server, plugged behind the real k8s client
/// through a DelegatingHandler. Serves LIST responses from typed mutable state,
/// serves watch streams (?watch=true) as line-delimited JSON fed by state mutations,
/// and counts every request per resource so tests can assert API-call reductions.
/// </summary>
public sealed class FakeKubernetesCluster
{
    private readonly object _gate = new();
    private readonly string _namespace;
    private long _resourceVersion;

    private readonly List<V1Deployment> _deployments = new();
    private readonly List<V1Pod> _pods = new();
    private readonly List<V1StatefulSet> _statefulSets = new();
    private readonly List<V1Job> _jobs = new();
    private readonly List<V1CronJob> _cronJobs = new();

    private readonly ConcurrentDictionary<string, List<WatchStreamContent>> _watchStreams = new();
    private readonly ConcurrentDictionary<string, int> _listCounts = new();
    private readonly ConcurrentDictionary<string, int> _watchConnectionCounts = new();

    public FakeKubernetesCluster(string kubeNamespace)
    {
        _namespace = kubeNamespace;
        Handler = new RoutingHandler(this);
    }

    public DelegatingHandler Handler { get; }

    public int GetListCount(string resource) => _listCounts.GetValueOrDefault(resource);

    public int TotalListCount => _listCounts.Values.Sum();

    public int TotalWatchConnections => _watchConnectionCounts.Values.Sum();

    public IReadOnlyDictionary<string, int> ListCounts => _listCounts;

    // ── Mutations (each bumps the resourceVersion and emits a watch event) ───────

    public void AddFunctionDeployment(string name, int replicas)
    {
        lock (_gate)
        {
            var deployment = new V1Deployment
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name,
                    NamespaceProperty = _namespace,
                    ResourceVersion = NextResourceVersion()
                },
                Spec = new V1DeploymentSpec
                {
                    Replicas = replicas,
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Annotations = new Dictionary<string, string> { ["SlimFaas/Function"] = "true" }
                        }
                    }
                }
            };
            _deployments.Add(deployment);
            Emit("deployments", "ADDED", deployment);
        }
    }

    public void SetDeploymentReplicas(string name, int replicas)
    {
        lock (_gate)
        {
            V1Deployment deployment = _deployments.Single(d => d.Metadata.Name == name);
            deployment.Spec.Replicas = replicas;
            deployment.Metadata.ResourceVersion = NextResourceVersion();
            Emit("deployments", "MODIFIED", deployment);
        }
    }

    public void AddFunctionPod(string deploymentName, string podName, string ip, bool ready = true)
    {
        lock (_gate)
        {
            var pod = BuildPod(podName, ip, ready);
            pod.Metadata.OwnerReferences = [new V1OwnerReference { Name = $"{deploymentName}-replicaset", Kind = "ReplicaSet" }];
            _pods.Add(pod);
            Emit("pods", "ADDED", pod);
        }
    }

    public void SetPodReady(string podName, bool ready)
    {
        lock (_gate)
        {
            V1Pod pod = _pods.Single(p => p.Metadata.Name == podName);
            ApplyReadyState(pod, ready);
            pod.Metadata.ResourceVersion = NextResourceVersion();
            Emit("pods", "MODIFIED", pod);
        }
    }

    public void AddJob(string jobName, string podIp)
    {
        lock (_gate)
        {
            var job = new V1Job
            {
                Metadata = new V1ObjectMeta
                {
                    Name = jobName,
                    NamespaceProperty = _namespace,
                    ResourceVersion = NextResourceVersion(),
                    Labels = new Dictionary<string, string>
                    {
                        ["slimfaas-job-element-id"] = $"element-{jobName}",
                        ["slimfaas-in-queue-timestamp"] = "100",
                        ["slimfaas-job-start-timestamp"] = "200"
                    }
                },
                Status = new V1JobStatus { Active = 1 }
            };
            _jobs.Add(job);
            Emit("jobs", "ADDED", job);

            var pod = BuildPod($"{jobName}-pod", podIp, ready: true);
            pod.Metadata.Labels = new Dictionary<string, string> { ["slimfaas-job-name"] = jobName };
            // Pas d'OwnerReferences déploiement : MapPodInformations l'ignore (pod de job).
            _pods.Add(pod);
            Emit("pods", "ADDED", pod);
        }
    }

    public void CompleteJob(string jobName)
    {
        lock (_gate)
        {
            V1Job job = _jobs.Single(j => j.Metadata.Name == jobName);
            // Comme le vrai API server : la terminaison d'un Job est signalée par la
            // condition terminale Complete=True (le compteur Succeeded ne suffit pas).
            job.Status = new V1JobStatus
            {
                Succeeded = 1,
                Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }]
            };
            job.Metadata.ResourceVersion = NextResourceVersion();
            Emit("jobs", "MODIFIED", job);
        }
    }

    public void AddCronJobConfiguration(string name, string image)
    {
        lock (_gate)
        {
            var cronJob = new V1CronJob
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name,
                    NamespaceProperty = _namespace,
                    ResourceVersion = NextResourceVersion(),
                    Annotations = new Dictionary<string, string> { ["SlimFaas/Job"] = "true" }
                },
                Spec = new V1CronJobSpec
                {
                    Schedule = "0 0 1 1 *",
                    Suspend = true,
                    JobTemplate = new V1JobTemplateSpec
                    {
                        Spec = new V1JobSpec
                        {
                            Template = new V1PodTemplateSpec
                            {
                                Spec = new V1PodSpec
                                {
                                    Containers =
                                    [
                                        new V1Container
                                        {
                                            Name = name,
                                            Image = image,
                                            Resources = new V1ResourceRequirements
                                            {
                                                Requests = new Dictionary<string, ResourceQuantity>
                                                {
                                                    ["cpu"] = new("100m"),
                                                    ["memory"] = new("100Mi")
                                                },
                                                Limits = new Dictionary<string, ResourceQuantity>
                                                {
                                                    ["cpu"] = new("100m"),
                                                    ["memory"] = new("100Mi")
                                                }
                                            }
                                        }
                                    ]
                                }
                            }
                        }
                    }
                }
            };
            _cronJobs.Add(cronJob);
            Emit("cronjobs", "ADDED", cronJob);
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────────

    private static V1Pod BuildPod(string podName, string ip, bool ready)
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                ResourceVersion = "1"
            },
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container
                    {
                        Name = "main",
                        Ports = [new V1ContainerPort { ContainerPort = 8080 }]
                    }
                ]
            },
            Status = new V1PodStatus { PodIP = ip }
        };
        ApplyReadyState(pod, ready);
        return pod;
    }

    private static void ApplyReadyState(V1Pod pod, bool ready)
    {
        pod.Status.ContainerStatuses =
        [
            new V1ContainerStatus { Name = "main", Started = ready, Ready = ready, State = new V1ContainerState() }
        ];
        pod.Status.Conditions =
        [
            new V1PodCondition { Type = "ContainersReady", Status = ready ? "True" : "False" },
            new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }
        ];
    }

    private string NextResourceVersion() => Interlocked.Increment(ref _resourceVersion).ToString();

    private void Emit(string resource, string type, IKubernetesObject obj)
    {
        string json = KubernetesJson.Serialize(obj);
        string line = $"{{\"type\":\"{type}\",\"object\":{json}}}";
        if (_watchStreams.TryGetValue(resource, out List<WatchStreamContent>? streams))
        {
            lock (streams)
            {
                foreach (WatchStreamContent stream in streams)
                {
                    // Fidélité au vrai API server : un watch filtré par labelSelector
                    // ne reçoit que les événements des objets qui le satisfont.
                    if (MatchesLabelSelector(stream.LabelSelector, obj))
                    {
                        stream.WriteLine(line);
                    }
                }
            }
        }
    }

    // Seules les deux formes utilisées par SlimFaas sont supportées : « le label
    // existe » (key) et « le label est absent » (!key).
    private static bool MatchesLabelSelector(string? selector, IKubernetesObject obj)
    {
        if (string.IsNullOrEmpty(selector))
        {
            return true;
        }

        IDictionary<string, string>? labels = (obj as IMetadata<V1ObjectMeta>)?.Metadata?.Labels;
        if (selector.StartsWith('!'))
        {
            return labels == null || !labels.ContainsKey(selector[1..]);
        }

        return labels != null && labels.ContainsKey(selector);
    }

    private static string? ExtractLabelSelector(string path)
    {
        const string marker = "labelSelector=";
        int start = path.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = path.IndexOf('&', start);
        string raw = end < 0 ? path[start..] : path[start..end];
        return Uri.UnescapeDataString(raw);
    }

    private IEnumerable<IKubernetesObject> CurrentObjectsOf(string resource) => resource switch
    {
        "deployments" => _deployments,
        "statefulsets" => _statefulSets,
        "cronjobs" => _cronJobs,
        "jobs" => _jobs,
        "pods" => _pods,
        _ => []
    };

    private static string ResourceOf(string path)
    {
        if (path.Contains("/cronjobs", StringComparison.Ordinal)) return "cronjobs";
        if (path.Contains("/jobs", StringComparison.Ordinal)) return "jobs";
        if (path.Contains("/deployments", StringComparison.Ordinal)) return "deployments";
        if (path.Contains("/statefulsets", StringComparison.Ordinal)) return "statefulsets";
        if (path.Contains("/services", StringComparison.Ordinal)) return "services";
        if (path.Contains("/pods", StringComparison.Ordinal)) return "pods";
        return "unknown";
    }

    private HttpResponseMessage Handle(HttpRequestMessage request)
    {
        string path = request.RequestUri!.PathAndQuery;
        string resource = ResourceOf(path);

        if (path.Contains("watch=true", StringComparison.Ordinal))
        {
            _watchConnectionCounts.AddOrUpdate(resource, 1, static (_, count) => count + 1);
            var content = new WatchStreamContent(ExtractLabelSelector(path));
            List<WatchStreamContent> streams = _watchStreams.GetOrAdd(resource, static _ => new List<WatchStreamContent>());
            lock (_gate)
            {
                lock (streams)
                {
                    streams.Add(content);
                }

                // Fidélité au vrai API server : un watch SANS resourceVersion reçoit
                // l'état courant sous forme d'événements ADDED synthétiques — rien
                // n'est perdu entre le démarrage du process et l'ouverture du flux.
                if (!path.Contains("resourceVersion=", StringComparison.Ordinal))
                {
                    foreach (IKubernetesObject existing in CurrentObjectsOf(resource))
                    {
                        if (MatchesLabelSelector(content.LabelSelector, existing))
                        {
                            content.WriteLine($"{{\"type\":\"ADDED\",\"object\":{KubernetesJson.Serialize(existing)}}}");
                        }
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
        }

        _listCounts.AddOrUpdate(resource, 1, static (_, count) => count + 1);

        string json;
        lock (_gate)
        {
            json = resource switch
            {
                "deployments" => KubernetesJson.Serialize(new V1DeploymentList { Items = _deployments.ToList() }),
                "statefulsets" => KubernetesJson.Serialize(new V1StatefulSetList { Items = _statefulSets.ToList() }),
                "services" => KubernetesJson.Serialize(new V1ServiceList { Items = [] }),
                "cronjobs" => KubernetesJson.Serialize(new V1CronJobList { Items = _cronJobs.ToList() }),
                "jobs" => KubernetesJson.Serialize(new V1JobList { Items = _jobs.ToList() }),
                "pods" => KubernetesJson.Serialize(new V1PodList { Items = FilterPods(path) }),
                _ => "{}"
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };
    }

    private List<V1Pod> FilterPods(string path)
    {
        // Le LIST des pods de jobs utilise labelSelector=slimfaas-job-name.
        if (path.Contains("labelSelector=slimfaas-job-name", StringComparison.Ordinal))
        {
            return _pods
                .Where(p => p.Metadata.Labels != null && p.Metadata.Labels.ContainsKey("slimfaas-job-name"))
                .ToList();
        }

        return _pods.ToList();
    }

    private sealed class RoutingHandler(FakeKubernetesCluster cluster) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(cluster.Handle(request));
    }

    // HttpContent branché sur un Pipe : les événements watch écrits par les mutations
    // sont visibles immédiatement côté lecteur (pas de bufferisation).
    private sealed class WatchStreamContent(string? labelSelector) : HttpContent
    {
        private readonly Pipe _pipe = new();

        /// <summary>Sélecteur de label (décodé) du watch, null si non filtré.</summary>
        public string? LabelSelector { get; } = labelSelector;

        public void WriteLine(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            _ = _pipe.Writer.WriteAsync(bytes).AsTask();
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _pipe.Reader.CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(_pipe.Reader.AsStream());

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
