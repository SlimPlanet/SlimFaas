using System.Text;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Integration;

[CollectionDefinition("KubernetesSyncIntegration", DisableParallelization = true)]
public sealed class KubernetesSyncIntegrationCollection;

// End-to-end non-regression test: the SAME scripted cluster scenario is executed
// against the real synchronization stack in polling mode and in watch mode, and the
// observed state snapshots (deployments, jobs, jobs configuration) must be identical
// at every checkpoint — proving the watch-driven path changes only the trigger, not
// the synchronized state.
[Collection("KubernetesSyncIntegration")]
public sealed class KubernetesSyncIntegrationShould
{
    [Fact]
    public async Task ProduceIdenticalStateInPollingAndWatchModes()
    {
        List<string> pollingSnapshots = await RunScenarioAsync(watchEnabled: false);
        List<string> watchSnapshots = await RunScenarioAsync(watchEnabled: true);

        Assert.Equal(pollingSnapshots.Count, watchSnapshots.Count);
        for (var checkpoint = 0; checkpoint < pollingSnapshots.Count; checkpoint++)
        {
            Assert.Equal(pollingSnapshots[checkpoint], watchSnapshots[checkpoint]);
        }
    }

    private static async Task<List<string>> RunScenarioAsync(bool watchEnabled)
    {
        var cluster = new FakeKubernetesCluster(KubernetesSyncStack.Namespace);
        // État initial : une fonction avec 2 pods prêts.
        cluster.AddFunctionDeployment("fibonacci", replicas: 2);
        cluster.AddFunctionPod("fibonacci", "fibonacci-pod-0", "10.0.0.1");
        cluster.AddFunctionPod("fibonacci", "fibonacci-pod-1", "10.0.0.2");

        var snapshots = new List<string>();
        await using var stack = new KubernetesSyncStack(
            cluster,
            watchEnabled,
            pollingCadenceMilliseconds: 100);
        await stack.StartAsync();

        // Checkpoint 1 — état initial synchronisé.
        await KubernetesSyncStack.WaitUntilAsync(
            () => FindFunction(stack, "fibonacci") is { } f && f.Pods.Count == 2,
            "initial deployment with 2 pods");
        snapshots.Add(Snapshot(stack));

        // Checkpoint 2 — scale-up + nouveau pod.
        cluster.SetDeploymentReplicas("fibonacci", 3);
        cluster.AddFunctionPod("fibonacci", "fibonacci-pod-2", "10.0.0.3");
        await KubernetesSyncStack.WaitUntilAsync(
            () => FindFunction(stack, "fibonacci") is { Replicas: 3 } f && f.Pods.Count == 3,
            "scale-up to 3 pods");
        snapshots.Add(Snapshot(stack));

        // Checkpoint 3 — un pod passe NotReady.
        cluster.SetPodReady("fibonacci-pod-0", false);
        await KubernetesSyncStack.WaitUntilAsync(
            () => FindFunction(stack, "fibonacci")?.Pods.Any(p => p.Ready == false) == true,
            "pod-0 not ready");
        snapshots.Add(Snapshot(stack));

        // Checkpoint 4 — création d'un job avec son pod.
        cluster.AddJob("fib-slimfaas-job-0001", "10.0.1.1");
        await KubernetesSyncStack.WaitUntilAsync(
            () => stack.JobService.Jobs.Any(j => j.Name == "fib-slimfaas-job-0001" && j.Status == JobStatus.Running),
            "job running");
        snapshots.Add(Snapshot(stack));

        // Checkpoint 5 — le job se termine.
        cluster.CompleteJob("fib-slimfaas-job-0001");
        await KubernetesSyncStack.WaitUntilAsync(
            () => stack.JobService.Jobs.Any(j => j.Name == "fib-slimfaas-job-0001" && j.Status == JobStatus.Succeeded),
            "job succeeded");
        snapshots.Add(Snapshot(stack));

        // Checkpoint 6 — nouvelle configuration de job (CronJob).
        cluster.AddCronJobConfiguration("myjob", "registry/image:1.0");
        await KubernetesSyncStack.WaitUntilAsync(
            () => stack.JobConfiguration.Configuration.Configurations.ContainsKey("myjob"),
            "cronjob configuration");
        snapshots.Add(Snapshot(stack));

        return snapshots;
    }

    private static DeploymentInformation? FindFunction(KubernetesSyncStack stack, string name) =>
        stack.ReplicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == name);

    // Normalisation structurelle des trois caches en une chaîne comparable :
    // seul l'état observable est capturé (pas les resourceVersions internes,
    // différentes entre modes uniquement par le nombre de LIST exécutés).
    private static string Snapshot(KubernetesSyncStack stack)
    {
        var builder = new StringBuilder();

        builder.AppendLine("functions:");
        foreach (DeploymentInformation function in stack.ReplicasService.Deployments.Functions
                     .OrderBy(f => f.Deployment, StringComparer.Ordinal))
        {
            builder.Append("  ").Append(function.Deployment)
                .Append(" replicas=").Append(function.Replicas)
                .Append(" pods=[");
            foreach (PodInformation pod in function.Pods.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                builder.Append(pod.Name)
                    .Append(":ready=").Append(pod.Ready)
                    .Append(":ip=").Append(pod.Ip)
                    .Append(':');
            }

            builder.AppendLine("]");
        }

        builder.AppendLine("jobs:");
        foreach (Job job in stack.JobService.Jobs.OrderBy(j => j.Name, StringComparer.Ordinal))
        {
            builder.Append("  ").Append(job.Name)
                .Append(" status=").Append(job.Status)
                .Append(" ips=[").AppendJoin(',', job.Ips.OrderBy(ip => ip, StringComparer.Ordinal))
                .Append("] elementId=").Append(job.ElementId)
                .AppendLine();
        }

        builder.AppendLine("jobsConfiguration:");
        foreach (string key in stack.JobConfiguration.Configuration.Configurations.Keys
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("  ").Append(key.ToLowerInvariant())
                .Append(" image=").Append(stack.JobConfiguration.Configuration.Configurations[key].Image)
                .AppendLine();
        }

        return builder.ToString();
    }
}
