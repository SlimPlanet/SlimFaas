using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace SlimFaas.Kubernetes;

public partial class KubernetesService
{
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
            if (e.SecretRef is not null)
            {
                return new V1EnvVar
                {
                    Name = e.Name,
                    ValueFrom = new V1EnvVarSource
                    {
                        SecretKeyRef = new V1SecretKeySelector
                        {
                            Name = e.SecretRef.Name,
                            Key = e.SecretRef.Key
                        }
                    }
                };
            }

            if (e.ConfigMapRef is not null)
            {
                return new V1EnvVar
                {
                    Name = e.Name,
                    ValueFrom = new V1EnvVarSource
                    {
                        ConfigMapKeyRef = new V1ConfigMapKeySelector
                        {
                            Name = e.ConfigMapRef.Name,
                            Key = e.ConfigMapRef.Key
                        }
                    }
                };
            }

            if (e.FieldRef is not null)
            {
                return new V1EnvVar
                {
                    Name = e.Name,
                    ValueFrom = new V1EnvVarSource
                    {
                        FieldRef = new V1ObjectFieldSelector
                        {
                            FieldPath = e.FieldRef.FieldPath
                        }
                    }
                };
            }

            if (e.ResourceFieldRef is not null)
            {
                return new V1EnvVar
                {
                    Name = e.Name,
                    ValueFrom = new V1EnvVarSource
                    {
                        ResourceFieldRef = new V1ResourceFieldSelector
                        {
                            ContainerName = e.ResourceFieldRef.ContainerName,
                            Resource = e.ResourceFieldRef.Resource,
                            Divisor = new ResourceQuantity(e.ResourceFieldRef.Divisor)
                        }
                    }
                };
            }

            // Valeur directe
            return new V1EnvVar
            {
                Name = e.Name,
                Value = e.Value
            };
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

        _logger.LogInformation("Job created with name: {JobName}", jobResponse.Metadata.Name);
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

        using HttpRequestMessage httpRequest = new(HttpMethod.Delete, new Uri(url));

        // 2. (body facultatif) : DeleteOptions
        //    Utile si vous voulez, par ex., gracePeriodSeconds = 0
        // var body = """
        //            {"kind":"DeleteOptions","apiVersion":"v1",
        //             "propagationPolicy":"Foreground","gracePeriodSeconds":0}
        //            """;
        // httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (client.Credentials is not null)
            await client.Credentials.ProcessHttpRequestAsync(httpRequest, CancellationToken.None);

        using HttpResponseMessage response = await client.HttpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode is not (HttpStatusCode.OK
            or HttpStatusCode.Accepted
            or HttpStatusCode.NoContent))
        {
            throw new HttpOperationException(
                $"Erreur pendant la suppression du Job {jobName} : {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }
}
