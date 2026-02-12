﻿using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

public class SlimDataSynchronizationWorker(
    IReplicasService replicasService,
    IRaftCluster cluster,
    ILogger<SlimDataSynchronizationWorker> logger,
    ISlimDataStatus slimDataStatus,
    IOptions<SlimFaasOptions> slimFaasOptions,
    IOptions<WorkersOptions> workersOptions,
    INamespaceProvider namespaceProvider)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.ReplicasSynchronizationDelayMilliseconds;
    private readonly string _baseSlimDataUrl = slimFaasOptions.Value.BaseSlimDataUrl;
    private readonly string _namespace = namespaceProvider.CurrentNamespace;


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SlimDataSynchronizationWorker: Start");
        await slimDataStatus.WaitForReadyAsync();
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);
                // Start SlimData only when 2 replicas are in ready state
                if (cluster.LeadershipToken.IsCancellationRequested)
                {
                    continue;
                }

                bool isWaitForNextRound = false;
                foreach (PodInformation slimFaasPod in replicasService.Deployments.SlimFaas.Pods.Where(p =>
                             p.Started == true && !string.IsNullOrEmpty(p.Ip)))
                {
                    string url = SlimDataEndpoint.Get(slimFaasPod, _baseSlimDataUrl, _namespace);
                    if (cluster.Members.ToList().Exists(m => SlimFaasPorts.RemoveLastPathSegment(m.EndPoint.ToString()) == SlimFaasPorts.RemoveLastPathSegment(url)))
                    {
                        continue;
                    }

                    logger.LogInformation($"SlimDataSynchronizationWorker: SlimFaas pod {slimFaasPod.Name} has to be added in the cluster");
                    await ((IRaftHttpCluster)cluster).AddMemberAsync(new Uri(url), stoppingToken);

                    // Add only one at once to let a synchronization time
                    isWaitForNextRound = true;
                    break;
                }

                if (isWaitForNextRound)
                {
                    continue;
                }

                // We remove extra replicas only if all desired replicas are not in the cluster
                if(replicasService.Deployments.SlimFaas.Replicas >= cluster.Members.Count)
                {
                    continue;
                }

                foreach (var endpoint in cluster.Members.Select(r => r.EndPoint.ToString()))
                {
                    if (replicasService.Deployments.SlimFaas.Pods.ToList().Exists(slimFaasPod =>
                            SlimFaasPorts.RemoveLastPathSegment(SlimDataEndpoint.Get(slimFaasPod, _baseSlimDataUrl, _namespace)) ==
                            SlimFaasPorts.RemoveLastPathSegment(endpoint)))
                    {
                        continue;
                    }

                    logger.LogInformation(
                        $"SlimDataSynchronizationWorker: SlimFaas pod {endpoint} need to be remove from the cluster");
                    await ((IRaftHttpCluster)cluster).RemoveMemberAsync(
                        new Uri(endpoint ?? string.Empty), stoppingToken);
                    // Remove only one at once to let a synchronization time
                    break;
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in SlimDataSynchronizationWorker");
            }
        }
    }
}
