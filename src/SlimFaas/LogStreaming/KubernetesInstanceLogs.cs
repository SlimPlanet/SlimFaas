using System.Net;
using System.Runtime.CompilerServices;
using k8s;
using k8s.Autorest;
using k8s.Models;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Logs;

public sealed class KubernetesInstanceLogs(k8s.Kubernetes client, IReplicasService replicas,
    IJobService jobs, INamespaceProvider namespaceProvider) : IInstanceLogProvider
{
    public async Task<LogSources> GetSourcesAsync(LogTarget target, CancellationToken ct)
    {
        try { return await DiscoverAsync(target, ct); }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Forbidden)
        { throw new UnauthorizedAccessException("Log access denied", ex); }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        { return new LogSources("Logs unavailable", []); }
    }

    private async Task<LogSources> DiscoverAsync(LogTarget target, CancellationToken ct)
    {
        string ns = namespaceProvider.CurrentNamespace;
        var pods = new List<V1Pod>();
        if (target.Kind == "job")
        {
            foreach (var job in jobs.Jobs.Where(j => j.Name.StartsWith(target.Name + KubernetesService.SlimfaasJobKey, StringComparison.Ordinal)
                         && (target.Replica is null || j.Name == target.Replica)))
            {
                var owner = await client.ReadNamespacedJobAsync(job.Name, ns, cancellationToken: ct);
                var list = await client.ListNamespacedPodAsync(ns,
                    labelSelector: $"slimfaas-job-name={job.Name}", cancellationToken: ct);
                pods.AddRange(list.Items.Where(p => p.Metadata.OwnerReferences?.Any(r => r.Kind == "Job" && r.Uid == owner.Metadata.Uid) == true));
            }
        }
        else
        {
            var function = replicas.Deployments.Functions.FirstOrDefault(f => f.Deployment == target.Name && f.Namespace == ns);
            var candidates = target.Kind == "slimfaas" && target.Name == "slimfaas"
                ? replicas.Deployments.SlimFaas.Pods
                : target.Kind == "function" ? function?.Pods ?? [] : [];
            foreach (var candidate in candidates.Where(p => target.Replica is null || p.Name == target.Replica))
            {
                var pod = await client.ReadNamespacedPodAsync(candidate.Name, ns, cancellationToken: ct);
                var controller = pod.Metadata.OwnerReferences?.FirstOrDefault(r => r.Controller == true);
                if (controller?.Kind == "ReplicaSet")
                {
                    var rs = await client.ReadNamespacedReplicaSetAsync(controller.Name, ns, cancellationToken: ct);
                    if (rs.Metadata.Uid != controller.Uid) continue;
                    controller = rs.Metadata.OwnerReferences?.FirstOrDefault(r => r.Controller == true);
                }
                if (controller?.Name != target.Name) continue;
                if (controller.Kind == "Deployment")
                {
                    var owner = await client.ReadNamespacedDeploymentAsync(target.Name, ns, cancellationToken: ct);
                    if (owner.Metadata.Uid == controller.Uid) pods.Add(pod);
                }
                else if (controller.Kind == "StatefulSet")
                {
                    var owner = await client.ReadNamespacedStatefulSetAsync(target.Name, ns, cancellationToken: ct);
                    if (owner.Metadata.Uid == controller.Uid) pods.Add(pod);
                }
            }
        }
        var sources = pods.OrderBy(p => p.Metadata.Name, StringComparer.Ordinal).SelectMany(p =>
            p.Spec.Containers.Select(c => LogSourceIds.Create(new LogSourceKey(target, p.Metadata.Name, c.Name,
                $"{p.Metadata.Uid}/{p.Status?.ContainerStatuses?.FirstOrDefault(s => s.Name == c.Name)?.RestartCount ?? 0}")))).ToArray();
        return new LogSources(sources.Length == 0 ? "Logs unavailable" : "Available", sources);
    }

    public async IAsyncEnumerable<LogReadItem> ReadAsync(string source, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = LogSourceIds.Parse(source);
        if (!(await GetSourcesAsync(key.Target, ct)).Sources.Any(s => s.Id == source))
            throw new FileNotFoundException("Source removed or restarted");
        Stream stream;
        try
        {
            stream = await client.ReadNamespacedPodLogAsync(key.Instance, namespaceProvider.CurrentNamespace,
                container: key.Container, follow: true, tailLines: LogLimits.Lines, timestamps: true, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Forbidden)
        { throw new UnauthorizedAccessException("Log access denied", ex); }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        { throw new FileNotFoundException("Source removed", ex); }
        await using (stream)
            await foreach (var line in LogText.ReadLinesAsync(stream, ct)) yield return line;
    }
}
