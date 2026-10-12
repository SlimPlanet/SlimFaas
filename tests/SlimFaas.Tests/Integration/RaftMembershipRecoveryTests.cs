using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimData.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using Xunit.Abstractions;

namespace SlimFaas.Tests.Integration;

[CollectionDefinition("RaftMembershipRecovery", DisableParallelization = true)]
public sealed class RaftMembershipRecoveryCollection;

[Collection("RaftMembershipRecovery")]
public sealed class RaftMembershipRecoveryTests(ITestOutputHelper output)
{
    [Theory(Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistent_cluster_recovers_after_pod_replacement_and_offline_member_removal(bool removeWhileOffline)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        string directory = Path.Combine(Path.GetTempPath(), $"slimfaas-membership-{Guid.NewGuid():N}");
        int[] ports = ReservePorts();
        var hosts = new IHost?[3];
        var pods = ports.Select((port, index) => new PodInformation(
            $"slimfaas-{index}", true, false, "127.0.0.1", "slimfaas", [port])).ToList();
        var topology = new SlimFaasDeploymentInformation(3, pods);
        var replicas = new Mock<IReplicasService>(MockBehavior.Strict);
        replicas.SetupGet(x => x.Deployments).Returns(() => new DeploymentsInformations([], topology, []));
        var elapsed = Stopwatch.StartNew();
        void Phase(string phase) => output.WriteLine($"{elapsed.Elapsed.TotalSeconds:F1}s: {phase}");

        try
        {
            Phase("bootstrap three persistent members");
            for (int index = 0; index < hosts.Length; index++)
            {
                hosts[index] = await StartAsync(index, coldStart: index == 0);
                if (index > 0)
                {
                    IHost leader = await LeaderAsync();
                    Assert.True(await Coordinator(leader).AddMemberAsync(Cluster(hosts[index]!).LocalMemberAddress, token));
                }
            }

            await AllMembersAsync(3);
            await WriteAsync("before-replacement");
            await VerifyAsync("before-replacement");

            IHost originalLeader = await LeaderAsync();
            int replacementIndex = Array.FindIndex(hosts, host => !ReferenceEquals(host, originalLeader));
            PodInformation replacementPod = pods[replacementIndex];
            await StopAsync(replacementIndex);
            pods.Remove(replacementPod);
            if (removeWhileOffline)
                topology = topology with { Replicas = 2 };

            Phase(removeWhileOffline ? "scale down while member is offline" : "reconcile repeated missing-pod observations");
            IHost leaderHost = await LeaderAsync();
            using (SlimDataMembershipReconciliationWorker worker = Worker(leaderHost))
            {
                // Six explicit observations exceed the configured three-cycle removal threshold
                // without relying on scheduler delays to reproduce the orchestration race.
                for (int cycle = 0; cycle < 6; cycle++)
                    await worker.ReconcileOnceAsync(token);
            }

            await AllMembersAsync(removeWhileOffline ? 2 : 3);
            await WriteAsync("during-replacement");
            await VerifyAsync("during-replacement");

            Phase("restart with the same WAL and membership files");
            topology = topology with { Replicas = 3 };
            hosts[replacementIndex] = await StartAsync(replacementIndex, coldStart: false);
            pods.Add(replacementPod);
            leaderHost = await LeaderAsync();
            using (SlimDataMembershipReconciliationWorker worker = Worker(leaderHost))
                await worker.ReconcileOnceAsync(token);
            await AllMembersAsync(3);
            await VerifyAsync("before-replacement", "during-replacement");

            Phase("replace the leader and re-elect with membership preserved");
            IHost previousLeader = await LeaderAsync();
            int leaderIndex = Array.IndexOf(hosts, previousLeader);
            PodInformation leaderPod = pods.Single(p => p.Name == $"slimfaas-{leaderIndex}");
            await StopAsync(leaderIndex);
            pods.Remove(leaderPod);
            leaderHost = await LeaderAsync();
            using (SlimDataMembershipReconciliationWorker worker = Worker(leaderHost))
            {
                for (int cycle = 0; cycle < 6; cycle++)
                    await worker.ReconcileOnceAsync(token);
            }
            await AllMembersAsync(3);
            await WriteAsync("after-election");
            hosts[leaderIndex] = await StartAsync(leaderIndex, coldStart: false);
            pods.Add(leaderPod);
            await AllMembersAsync(3);
            await VerifyAsync("before-replacement", "during-replacement", "after-election");
            Phase("all members agree and retain committed values");
        }
        finally
        {
            await Task.WhenAll(Enumerable.Range(0, hosts.Length).Select(StopAsync));
            Directory.Delete(directory, recursive: true);
        }

        async Task<IHost> StartAsync(int index, bool coldStart)
        {
            var configuration = new Dictionary<string, string?>
            {
                ["partitioning"] = "false",
                ["lowerElectionTimeout"] = "600",
                ["upperElectionTimeout"] = "1200",
                ["requestTimeout"] = "00:00:02",
                ["rpcTimeout"] = "00:00:01",
                ["warmupRounds"] = "512",
                ["publicEndPoint"] = $"http://127.0.0.1:{ports[index]}/",
                ["coldStart"] = coldStart.ToString(),
                [SlimPersistentState.LogLocation] = Path.Combine(directory, $"node-{index}"),
                [SlimPersistentState.UsePersistentConfigurationStorage] = "true"
            };
            IHost host = new HostBuilder()
                .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration))
                .ConfigureLogging(builder => builder.ClearProviders())
                .ConfigureWebHost(builder => builder
                    .UseKestrel(options => options.Listen(IPAddress.Loopback, ports[index]))
                    .UseStartup<Startup>())
                .JoinCluster()
                .Build();
            try
            {
                await host.Services.GetRequiredService<SlimPersistentState>().RestoreAsync(token);
                await host.StartAsync(token);
                return host;
            }
            catch
            {
                host.Dispose();
                throw;
            }
        }

        async Task StopAsync(int index)
        {
            if (hosts[index] is not { } host)
                return;
            hosts[index] = null;
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(shutdown.Token).WaitAsync(shutdown.Token);
            host.Dispose();
        }

        async Task<IHost> LeaderAsync()
        {
            IHost? leader = null;
            await UntilAsync(() => (leader = hosts.OfType<IHost>().FirstOrDefault(host =>
                !Cluster(host).LeadershipToken.IsCancellationRequested &&
                !Cluster(host).ConsensusToken.IsCancellationRequested &&
                Cluster(host).TryGetLeaseToken(out CancellationToken lease) && !lease.IsCancellationRequested)) is not null);
            return leader!;
        }

        Task AllMembersAsync(int count)
        {
            HashSet<Uri> expected = count == 3
                ? ports.Select(port => new Uri($"http://127.0.0.1:{port}/")).ToHashSet()
                : hosts.OfType<IHost>().Select(host => Cluster(host).LocalMemberAddress).ToHashSet();
            return UntilAsync(() => hosts.OfType<IHost>().All(host =>
                expected.SetEquals(((IRaftCluster)Cluster(host)).Members
                    .Select(member => ((UriEndPoint)member.EndPoint).Uri)) &&
                Cluster(host).Readiness.IsCompletedSuccessfully && Cluster(host).Leader is not null &&
                !Cluster(host).ConsensusToken.IsCancellationRequested) &&
                hosts.OfType<IHost>().Select(host => (Cluster(host).Leader?.EndPoint as UriEndPoint)?.Uri)
                    .Distinct().Count() == 1);
        }

        async Task UntilAsync(Func<bool> predicate)
        {
            while (!predicate())
                await Task.Delay(25, token);
        }

        async Task WriteAsync(string key)
        {
            IHost leader = await LeaderAsync();
            using var write = CancellationTokenSource.CreateLinkedTokenSource(token);
            KeyValueBatchResponse response = await SlimData.Endpoints.AddKeyValueBatchCommand(
                new KeyValueBatchRequest([new KeyValueBatchItem(KeyValueOperation.Set, key,
                    Encoding.UTF8.GetBytes($"value-{key}"), null, 0, 0, DateTime.UtcNow.Ticks)]),
                Cluster(leader), write);
            Assert.Equal(KeyValueCommandStatus.Applied, Assert.Single(response.Results).Status);
        }

        Task VerifyAsync(params string[] keys) => UntilAsync(() => hosts.OfType<IHost>().All(host =>
            keys.All(key => host.Services.GetRequiredService<SlimPersistentState>().SlimDataState.KeyValues
                .TryGetValue(key, out ReadOnlyMemory<byte> value) && Encoding.UTF8.GetString(value.Span) == $"value-{key}")));

        SlimDataMembershipReconciliationWorker Worker(IHost host)
        {
            var namespaceProvider = new Mock<INamespaceProvider>();
            namespaceProvider.SetupGet(x => x.CurrentNamespace).Returns("membership-test");
            return new(replicas.Object, Cluster(host), Coordinator(host),
                NullLogger<SlimDataMembershipReconciliationWorker>.Instance,
                Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions { BaseSlimDataUrl = "http://{pod_ip}:{pod_port_0}" }),
                Microsoft.Extensions.Options.Options.Create(new WorkersOptions()),
                Microsoft.Extensions.Options.Options.Create(new SlimDataMembershipOptions()), namespaceProvider.Object);
        }
    }

    private static IRaftHttpCluster Cluster(IHost host) => host.Services.GetRequiredService<IRaftHttpCluster>();

    private static ClusterMembershipCoordinator Coordinator(IHost host) => host.Services.GetRequiredService<ClusterMembershipCoordinator>();

    private static int[] ReservePorts()
    {
        TcpListener[] listeners = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        try
        {
            foreach (TcpListener listener in listeners)
                listener.Start();
            return listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (TcpListener listener in listeners)
                listener.Stop();
        }
    }
}
