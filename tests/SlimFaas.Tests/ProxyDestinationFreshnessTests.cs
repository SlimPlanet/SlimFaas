using SlimFaas.Kubernetes;

namespace SlimFaas.Tests;

public sealed class ProxyDestinationFreshnessTests
{
    [Fact]
    public void Replaced_pod_is_selected_from_current_destinations_after_configuration_was_captured()
    {
        var service = new FakeReplicasService();
        DeploymentInformation captured = Function([Pod("old", "10.0.0.1", 8080)]);
        service.Deployments.Functions.Add(captured);
        var proxy = new Proxy(service, captured.Deployment, captured);
        service.Deployments.Functions[0] = captured with { Pods = [Pod("replacement", "10.0.0.2", 8081)] };

        Assert.True(proxy.TryAcquireNextTargetForSync(out SyncTargetReservation target));
        try
        {
            Assert.Equal("10.0.0.2", target.Ip);
            Assert.Equal([8081], target.Ports);
        }
        finally
        {
            proxy.ReleaseSyncIP(target.RoutingTarget);
        }
    }

    [Fact]
    public void Deleted_pod_does_not_remain_available_through_a_captured_snapshot()
    {
        var service = new FakeReplicasService();
        DeploymentInformation captured = Function([Pod("old", "10.0.0.1", 8080)]);
        service.Deployments.Functions.Add(captured);
        var proxy = new Proxy(service, captured.Deployment, captured);
        service.Deployments.Functions[0] = captured with { Pods = [] };

        bool acquired = proxy.TryAcquireNextTargetForSync(out SyncTargetReservation target);
        try
        {
            Assert.False(acquired);
        }
        finally
        {
            if (acquired)
                proxy.ReleaseSyncIP(target.RoutingTarget);
        }
    }

    [Fact]
    public void Wakeup_can_select_a_ready_pod_after_an_empty_snapshot_was_captured()
    {
        var service = new FakeReplicasService();
        DeploymentInformation captured = Function([]);
        service.Deployments.Functions.Add(captured);
        var proxy = new Proxy(service, captured.Deployment, captured);
        service.Deployments.Functions[0] = captured with { Pods = [Pod("started", "10.0.0.3", 8080)] };

        Assert.True(proxy.TryAcquireNextTargetForSync(out SyncTargetReservation target));
        try
        {
            Assert.Equal("10.0.0.3", target.Ip);
        }
        finally
        {
            proxy.ReleaseSyncIP(target.RoutingTarget);
        }
    }

    private static DeploymentInformation Function(List<PodInformation> pods) =>
        new("freshness-" + Guid.NewGuid().ToString("N"), "tests", pods, new SlimFaasConfiguration(), 1);

    private static PodInformation Pod(string name, string ip, int port) =>
        new(name, true, true, ip, "function", [port]);
}
