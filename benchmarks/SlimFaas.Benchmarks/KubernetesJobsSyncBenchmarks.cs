using System.Net;
using System.Reflection;
using System.Text;
using BenchmarkDotNet.Attributes;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Kubernetes;

namespace SlimFaas.Benchmarks;

/// <summary>
/// Theme 7 — one Kubernetes jobs synchronization (<c>KubernetesService.ListJobsAsync</c>),
/// executed every second on every SlimFaas replica before PR #340 (and on every jobs
/// watch event / 30 s resync since).
/// The Kubernetes API is replaced by an in-memory handler that answers the job LIST and
/// the pod LIST(s) from pre-serialized JSON, honouring the <c>labelSelector</c> the
/// service sends (a per-job <c>slimfaas-job-name=&lt;job&gt;</c> selector only returns the
/// pods of that job, the label-exists selector returns every job pod), so both the N+1
/// strategy (one pod LIST per job) and the single-LIST strategy see the same data.
/// The measured time is the client-side cost (request building, JSON deserialization,
/// mapping); the number of API round trips per call is printed at the end of the run
/// and is what dominates the cost against a real API server.
/// (KubernetesService is materialized without its constructor, like the unit tests do,
/// so no kubeconfig is needed.)
/// </summary>
[MemoryDiagnoser]
public class KubernetesJobsSyncBenchmarks
{
    private const string JobNameLabel = "slimfaas-job-name";

    [Params(10, 50)]
    public int JobCount { get; set; }

    private KubernetesService _service = null!;
    private CountingHandler _handler = null!;
    private k8s.Kubernetes _client = null!;
    private long _callsBefore;

    [GlobalSetup]
    public void Setup()
    {
        _handler = new CountingHandler(BuildJobList(JobCount), BuildPods(JobCount));
        _client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost" }, _handler);

        _service = (KubernetesService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(KubernetesService));
        typeof(KubernetesService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_service, _client);
        typeof(KubernetesService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_service, NullLogger<KubernetesService>.Instance);

        // One untimed call to report the API round trips per synchronization.
        _handler.Requests = 0;
        _service.ListJobsAsync("bench").GetAwaiter().GetResult();
        Console.WriteLine(
            $"// KubernetesJobsSyncBenchmarks: ListJobsAsync issues {_handler.Requests} Kubernetes API request(s) per synchronization for {JobCount} jobs");
        _callsBefore = _handler.Requests;
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    [Benchmark]
    public Task<IList<Job>> ListJobs() => _service.ListJobsAsync("bench");

    private static V1JobList BuildJobList(int count)
    {
        var items = new List<V1Job>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(new V1Job
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"job-{i:D4}",
                    Annotations = new Dictionary<string, string> { ["SlimFaas/DependsOn"] = "dep-1,dep-2" },
                    Labels = new Dictionary<string, string>
                    {
                        ["slimfaas-job-element-id"] = $"element-{i:D4}",
                        ["slimfaas-in-queue-timestamp"] = "100",
                        ["slimfaas-job-start-timestamp"] = "200"
                    }
                },
                Status = new V1JobStatus { Active = 1 }
            });
        }

        return new V1JobList { Items = items };
    }

    private static List<V1Pod> BuildPods(int jobCount)
    {
        var pods = new List<V1Pod>(jobCount);
        for (var i = 0; i < jobCount; i++)
        {
            pods.Add(new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"job-{i:D4}-pod",
                    Labels = new Dictionary<string, string> { [JobNameLabel] = $"job-{i:D4}" }
                },
                Status = new V1PodStatus { PodIP = $"10.43.{i / 250}.{i % 250 + 1}", ContainerStatuses = [] }
            });
        }

        return pods;
    }

    /// <summary>
    /// In-memory Kubernetes API: serializes the job list once and the pod list per
    /// distinct selector (cached), and counts the requests.
    /// </summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        private readonly byte[] _jobs;
        private readonly List<V1Pod> _pods;
        private readonly Dictionary<string, byte[]> _podResponses = new(StringComparer.Ordinal);

        public CountingHandler(V1JobList jobs, List<V1Pod> pods)
        {
            _jobs = Encoding.UTF8.GetBytes(KubernetesJson.Serialize(jobs));
            _pods = pods;
        }

        public long Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            string path = request.RequestUri!.PathAndQuery;
            byte[] body = path.Contains("/pods", StringComparison.Ordinal) ? PodsFor(path) : _jobs;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
                RequestMessage = request
            });
        }

        private byte[] PodsFor(string path)
        {
            string selector = Uri.UnescapeDataString(SelectorOf(path));
            lock (_podResponses)
            {
                if (_podResponses.TryGetValue(selector, out byte[]? cached))
                    return cached;

                int equals = selector.IndexOf('=');
                IEnumerable<V1Pod> selected = equals < 0
                    ? _pods
                    : _pods.Where(p => p.Metadata.Labels[JobNameLabel] == selector[(equals + 1)..]);
                byte[] body = Encoding.UTF8.GetBytes(KubernetesJson.Serialize(new V1PodList { Items = selected.ToList() }));
                _podResponses[selector] = body;
                return body;
            }
        }

        private static string SelectorOf(string path)
        {
            const string key = "labelSelector=";
            int start = path.IndexOf(key, StringComparison.Ordinal);
            if (start < 0)
                return string.Empty;
            start += key.Length;
            int end = path.IndexOf('&', start);
            return end < 0 ? path[start..] : path[start..end];
        }
    }
}
