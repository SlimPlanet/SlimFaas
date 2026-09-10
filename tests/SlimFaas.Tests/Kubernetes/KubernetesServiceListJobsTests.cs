using System.Net;
using System.Reflection;
using System.Text;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

// Non-regression tests for ListJobsAsync after the N+1 fix: one pod LIST with a
// label-exists selector replaces one pod LIST per job. The behavior (statuses, IPs,
// DependsOn, timestamps) must stay identical, and the HTTP request count must not
// grow with the number of jobs.
public sealed class KubernetesServiceListJobsTests
{
    private const string JobNameLabel = "slimfaas-job-name";

    private static readonly string[] ExpectedJobAIps = ["10.0.0.1", "10.0.0.2"];
    private static readonly string[] ExpectedJobADependsOn = ["dep-1", "dep-2"];

    [Fact]
    public async Task ListJobsMakesExactlyTwoApiCallsRegardlessOfJobCount()
    {
        var handler = new RoutingHandler(
            BuildJobList("job-a", "job-b", "job-c"),
            BuildPodList(("job-a", "10.0.0.1", true), ("job-b", "10.0.0.2", false)));
        var service = BuildService(handler, out var client);
        using (client)
        {
            IList<Job> jobs = await service.ListJobsAsync("test");

            Assert.Equal(3, jobs.Count);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains(handler.Requests, path =>
                path.Contains("/apis/batch/v1/namespaces/test/jobs", StringComparison.Ordinal));
            string podRequest = Assert.Single(handler.Requests, path =>
                path.Contains("/api/v1/namespaces/test/pods", StringComparison.Ordinal));
            // Sélecteur « le label existe » : la clé seule, sans valeur.
            Assert.Contains($"labelSelector={JobNameLabel}", podRequest, StringComparison.Ordinal);
            Assert.DoesNotContain($"labelSelector={JobNameLabel}%3D", podRequest, StringComparison.Ordinal);
            Assert.DoesNotContain($"labelSelector={JobNameLabel}=", podRequest, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ListJobsMapsPodsToTheirJobs()
    {
        var handler = new RoutingHandler(
            BuildJobList("job-a", "job-b"),
            BuildPodList(("job-a", "10.0.0.1", false), ("job-a", "10.0.0.2", false), ("job-b", "10.0.0.3", true)));
        var service = BuildService(handler, out var client);
        using (client)
        {
            IList<Job> jobs = await service.ListJobsAsync("test");

            Job jobA = Assert.Single(jobs, j => j.Name == "job-a");
            Assert.Equal(ExpectedJobAIps, jobA.Ips);
            Assert.Equal(JobStatus.Running, jobA.Status);
            Assert.Equal(ExpectedJobADependsOn, jobA.DependsOn);

            // job-b a un pod en ImagePullBackOff : le statut doit le refléter.
            Job jobB = Assert.Single(jobs, j => j.Name == "job-b");
            Assert.Equal(JobStatus.ImagePullBackOff, jobB.Status);
        }
    }

    [Fact]
    public async Task ListJobsToleratesMalformedTimestampLabels()
    {
        V1JobList jobList = BuildJobList("job-a");
        jobList.Items[0].Metadata.Labels = new Dictionary<string, string>
        {
            ["slimfaas-job-element-id"] = "element-1",
            ["slimfaas-in-queue-timestamp"] = "not-a-number",
            ["slimfaas-job-start-timestamp"] = "42"
        };
        var handler = new RoutingHandler(jobList, BuildPodList());
        var service = BuildService(handler, out var client);
        using (client)
        {
            IList<Job> jobs = await service.ListJobsAsync("test");

            Job job = Assert.Single(jobs);
            Assert.Equal("element-1", job.ElementId);
            Assert.Equal(0, job.InQueueTimestamp);
            Assert.Equal(42, job.StartTimestamp);
        }
    }

    [Fact]
    public async Task ListJobsReturnsEmptyIpsForJobWithoutPods()
    {
        var handler = new RoutingHandler(BuildJobList("job-a"), BuildPodList());
        var service = BuildService(handler, out var client);
        using (client)
        {
            IList<Job> jobs = await service.ListJobsAsync("test");

            Job job = Assert.Single(jobs);
            Assert.Empty(job.Ips);
        }
    }

    private static V1JobList BuildJobList(params string[] names)
    {
        var items = names.Select(name => new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                Annotations = new Dictionary<string, string>
                {
                    ["SlimFaas/DependsOn"] = "dep-1,dep-2"
                },
                Labels = new Dictionary<string, string>
                {
                    ["slimfaas-job-element-id"] = $"element-{name}",
                    ["slimfaas-in-queue-timestamp"] = "100",
                    ["slimfaas-job-start-timestamp"] = "200"
                }
            },
            Status = new V1JobStatus { Active = 1 }
        }).ToList();
        return new V1JobList { Items = items };
    }

    private static V1PodList BuildPodList(params (string JobName, string Ip, bool ImagePullBackOff)[] pods)
    {
        var items = pods.Select(pod => new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"{pod.JobName}-pod",
                Labels = new Dictionary<string, string> { [JobNameLabel] = pod.JobName }
            },
            Status = new V1PodStatus
            {
                PodIP = pod.Ip,
                ContainerStatuses = pod.ImagePullBackOff
                    ?
                    [
                        new V1ContainerStatus
                        {
                            State = new V1ContainerState
                            {
                                Waiting = new V1ContainerStateWaiting { Reason = "ImagePullBackOff" }
                            }
                        }
                    ]
                    : []
            }
        }).ToList();
        return new V1PodList { Items = items };
    }

    private static KubernetesService BuildService(
        DelegatingHandler handler,
        out k8s.Kubernetes client)
    {
        var config = new KubernetesClientConfiguration { Host = "http://localhost" };
        client = new k8s.Kubernetes(config, handler);

        var service = (KubernetesService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(KubernetesService));

        typeof(KubernetesService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);
        typeof(KubernetesService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, NullLogger<KubernetesService>.Instance);

        return service;
    }

    private sealed class RoutingHandler(V1JobList jobList, V1PodList podList) : DelegatingHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.PathAndQuery;
            lock (Requests)
            {
                Requests.Add(path);
            }

            string json = path.Contains("/pods", StringComparison.Ordinal)
                ? KubernetesJson.Serialize(podList)
                : KubernetesJson.Serialize(jobList);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
