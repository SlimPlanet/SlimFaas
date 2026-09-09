using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Endpoints;

public sealed class StatusLeaderTests
{
    [Fact]
    public void Native_leader_matching_includes_ports_and_handles_election_and_unknown_members()
    {
        IList<PodInformation> pods = Enumerable.Range(0, 3).Select(n =>
            new PodInformation($"slimfaas-{n}", true, true, "127.0.0.1", "slimfaas", [3262 + n, 30021 + n])).ToList();
        const string pattern = "http://{pod_ip}:{pod_port_0}";
        foreach (int n in Enumerable.Range(0, 3))
            Assert.Equal($"slimfaas-{n}", StatusLeader.FindLeader(new Uri($"http://127.0.0.1:{3262 + n}/"), pods, pattern, "demo"));
        Assert.Null(StatusLeader.FindLeader(null, pods, pattern, "demo"));
        Assert.Null(StatusLeader.FindLeader(new Uri("http://127.0.0.1:9999"), pods, pattern, "demo"));
        Assert.Null(StatusLeader.FindLeader(new Uri("http://127.0.0.1:3262"), [pods[0], pods[0]], pattern, "demo"));
    }

    [Fact]
    public void Kubernetes_dns_leader_resolves_to_a_name_without_projecting_the_endpoint()
    {
        IList<PodInformation> pods = [new("slimfaas-1", true, true, "10.0.0.1", "slimfaas", ServiceName: "slimfaas")];
        Assert.Equal("slimfaas-1", StatusLeader.FindLeader(new Uri("http://slimfaas-1.slimfaas.demo.svc:3262/"), pods,
            "http://{pod_name}.{service_name}.{namespace}.svc:3262", "demo"));
    }
}
