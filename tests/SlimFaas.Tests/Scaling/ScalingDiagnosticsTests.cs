using System.Text;
using System.Text.Json;
using MemoryPack;
using Moq;
using Prometheus;
using SlimFaas.Kubernetes;
using SlimFaas.Scaling;

namespace SlimFaas.Tests.Scaling;

public sealed class ScalingDiagnosticsTests
{
    [Fact]
    public async Task PreviewUsesRuntimeMathAndDoesNotWriteTelemetryHistoriesMetricsRegistryOrReplicas()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73);
        var before = MemoryPackSerializer.Serialize(f.Metrics.CreateRecord());
        string telemetry = await Telemetry(f.Name);
        for (int i = 0; i < 3; i++)
        {
            var preview = await f.Simulation.SimulateAsync(new(f.Name, Triggers: [f.Override(20)]), default);
            Assert.Equal(8, preview.Current.RawTarget); Assert.Equal(4, preview.Current.Target);
            Assert.Equal(4, preview.Simulated.RawTarget); Assert.Equal(4, preview.Simulated.Target);
            Assert.Equal("Preview", preview.Simulated.Application);
        }
        Assert.Equal(before, MemoryPackSerializer.Serialize(f.Metrics.CreateRecord()));
        Assert.Equal(telemetry, await Telemetry(f.Name));
        Assert.Empty(f.Scaler.CaptureHistory(f.Name).Decisions);
        Assert.Empty(f.Scaler.CaptureHistory(f.Name).Recommendations);
        Assert.Equal(0, f.Http.GetTicksLastCall(f.Name));
        Assert.Equal(["jobs_pending"], f.Registry.GetRequestedMetricNames());
        f.Kubernetes.Verify(k => k.ScaleAsync(It.IsAny<ReplicaRequest>()), Times.Never);
        await f.Replicas.CheckScaleAsync("default");
        Assert.Equal(4, f.Replicas.Deployments.Functions[0].Replicas);
        var decision = f.State().Decision!;
        Assert.Equal(8, decision.RawTarget); Assert.Equal(4, decision.PolicyTarget);
        Assert.Equal("Accepted", decision.Application); Assert.Equal(4, decision.AcceptedReplicas);
        Assert.Contains(f.State().Events, e => e.Application == "Sent");
        Assert.Contains(decision.Reasons, r => r.Code == "PolicyLimited");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservabilityPreservesRuntimeDecisionSequence(bool observer)
    {
        var f = new ScalingTestFixture(observer); await f.SetFunctions(); f.Scrape(73);
        await f.Replicas.CheckScaleAsync("default"); Assert.Equal(4, f.Replicas.Deployments.Functions[0].Replicas);
        for (int i = 0; i < 5; i++)
        {
            if (observer) _ = f.State();
            await f.Replicas.CheckScaleAsync("default"); Assert.Equal(4, f.Replicas.Deployments.Functions[0].Replicas);
        }
        f.Time.Now = f.Time.Now.AddSeconds(16); f.Scrape(73);
        await f.Replicas.CheckScaleAsync("default"); Assert.Equal(8, f.Replicas.Deployments.Functions[0].Replicas);
    }

    [Fact]
    public async Task ExplicitPolicyPreviewCanWakeToEightAndHypotheticalZeroStaysDistinctFromNoData()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73);
        var behavior = new ScaleBehavior { ScaleUp = new() { Policies = [new(ScalePolicyType.Pods, 20)] },
            ScaleDown = new() { Policies = [] } };
        var preview = await f.Simulation.SimulateAsync(new(f.Name, Behavior: behavior), default);
        Assert.Equal(8, preview.Simulated.Target);
        var empty = await f.Simulation.SimulateAsync(new(f.Name, CurrentReplicas: 8,
            Behavior: behavior, Triggers: [f.Override(query: "not_collected")]), default);
        Assert.Equal(8, empty.Simulated.Target);
        Assert.Null(empty.Simulated.Triggers[0].Value);
        Assert.Contains("No usable matching data", empty.Simulated.Triggers[0].Detail);
        Assert.DoesNotContain("not_collected", f.Registry.GetRequestedMetricNames());
        var zero = await f.Simulation.SimulateAsync(new(f.Name, CurrentReplicas: 8,
            Behavior: behavior, Triggers: [f.Override(value: 0)]), default);
        Assert.Equal(0, zero.Simulated.Target);
        Assert.True(zero.Simulated.Triggers[0].Simulated); Assert.Equal(0, zero.Simulated.Triggers[0].Value);
    }

    [Fact]
    public async Task FailedSourceBlocksReductionAndSimulatedQueryCannotReadAnotherFunction()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(8); f.Scrape(73); f.Scrape(999, "another");
        var preview = await f.Simulation.SimulateAsync(new(f.Name), default);
        Assert.Equal(73, preview.Current.Triggers[0].Value);
        var source = ExternalMetricsSource.Resolve("default", f.Name, f.Config, "jobs")!;
        f.Sources.Record(source, ScalerState.Timeout, f.Time.Now.ToUnixTimeSeconds());
        var failed = await f.Simulation.SimulateAsync(new(f.Name), default);
        Assert.Equal(8, failed.Current.Target); Assert.Equal("Timeout", failed.Current.Triggers[0].State);
        Assert.Null(failed.Current.Triggers[0].Value);
    }

    [Fact]
    public async Task PreviewFreezesHttpDependenciesAndDoesNotConsumePolicyBudgetWhileWaiting()
    {
        var f = new ScalingTestFixture();
        await f.SetFunctions(functions: [new(f.Name, "default", [], new(), 0, Scale: f.Config, DependsOn: ["database"]),
            new("database", "default", [], new(), 0)]);
        f.Scrape(73);
        var preview = await f.Simulation.SimulateAsync(new(f.Name), default);
        Assert.Equal(0, preview.Current.Target); Assert.False(preview.Current.DependenciesReady);
        Assert.Contains(preview.Current.Reasons, r => r.Code == "DependenciesNotReady");
        Assert.Empty(f.History.GetSamples(f.Name, 0));
        Assert.Equal(0, f.Http.GetTicksLastCall("database"));
        await f.Replicas.CheckScaleAsync("default");
        Assert.Equal(0, f.Replicas.Deployments.Functions[0].Replicas);
        Assert.Equal(1, f.Replicas.Deployments.Functions[1].Replicas);
    }

    [Fact]
    public async Task DisabledWakeAndInfrastructureFailureAreExplained()
    {
        var f = new ScalingTestFixture(); f.Config = f.Config with { ScaleFromZero = false };
        await f.SetFunctions(); f.Scrape(73);
        var preview = await f.Simulation.SimulateAsync(new(f.Name), default);
        Assert.Equal(0, preview.Current.Target); Assert.Equal("NotEvaluated", preview.Current.Triggers[0].State);
        Assert.Contains(preview.Current.Reasons, r => r.Code == "ExternalWakeDisabled");
        var wake = await f.Simulation.SimulateAsync(new(f.Name, ScaleFromZero: true), default);
        Assert.Equal(4, wake.Simulated.Target);
        await f.SetFunctions(functions: [new(f.Name, "default", [new("blocked", false, false, "10.0.0.1", f.Name) { StartFailureReason = "Unschedulable" }], new(), 1, Scale: f.Config)]);
        var blocked = await f.Simulation.SimulateAsync(new(f.Name), default);
        Assert.Equal(1, blocked.Simulated.Target);
        Assert.Contains(blocked.Simulated.Reasons, r => r.Code == "InfrastructureBlocked");
    }

    [Fact]
    public async Task ApplicationFailureIsRecordedAndOriginalExceptionStillPropagates()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73);
        f.Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ThrowsAsync(new InvalidOperationException("orchestrator failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Replicas.CheckScaleAsync("default"));
        Assert.Equal("Failed", f.State().Decision!.Application);
        Assert.Empty(f.History.GetSamples(f.Name, 0));
    }

    [Fact]
    public async Task RetentionDeduplicatesBoundsEventsAndResetsOnLeadershipChange()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73); await f.Replicas.CheckScaleAsync("default");
        var decision = f.State().Decision!;
        for (int i = 0; i < 1000; i++) f.Diagnostics.Record(decision, f.Config);
        Assert.True(f.State().Events.Count < 10);
        for (int i = 0; i < 500; i++) f.Diagnostics.Record(decision with { Target = i }, f.Config);
        Assert.Equal(300, f.State().Events.Count); Assert.True(f.State().Truncated);
        Assert.InRange(f.Diagnostics.RetainedBytes, 1, ScalingDiagnosticsStore.MaxBytes);
        var session = f.State().Session; f.Diagnostics.SetLeadership(false); f.Diagnostics.SetLeadership(true);
        Assert.NotEqual(session, f.State().Session); Assert.Empty(f.State().Events); Assert.Null(f.State().Decision);
        f.Diagnostics.Record(decision, f.Config); f.Time.Now = f.Time.Now.AddMinutes(16);
        Assert.Empty(f.State().Events); Assert.Null(f.State().Decision); Assert.Equal(0, f.Diagnostics.RetainedBytes);
    }

    [Fact]
    public async Task RetentionEnforcesGlobalByteBudgetAcrossFunctionsAndRemovesDeletedFunctions()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73); await f.Replicas.CheckScaleAsync("default");
        var decision = f.State().Decision!;
        for (int i = 0; i < 1000; i++)
        {
            f.Diagnostics.Record(decision with { Function = "function-" + i,
                Reasons = [new("Reason", new string('x', 6000))] }, f.Config);
            Assert.InRange(f.Diagnostics.RetainedBytes, 0, ScalingDiagnosticsStore.MaxBytes);
        }
        f.Diagnostics.Retain(new HashSet<string>()); Assert.Equal(0, f.Diagnostics.RetainedBytes);
    }

    [Theory]
    [InlineData(-1, "sum(jobs_pending)", 10, "jobs")]
    [InlineData(0, "sum(", 10, "jobs")]
    [InlineData(0, "jobs_pending{queue=~\"[\"}", 10, "jobs")]
    [InlineData(0, "sum(jobs_pending)", 0, "jobs")]
    [InlineData(0, "sum(jobs_pending)", 10, "unknown")]
    public async Task InvalidOverridesAreRejected(int current, string query, double threshold, string source)
    {
        var f = new ScalingTestFixture(); await f.SetFunctions();
        var exception = await Assert.ThrowsAsync<ScalingSimulationException>(() => f.Simulation.SimulateAsync(new(f.Name,
            CurrentReplicas: current, Triggers: [f.Override(threshold, query: query, source: source)]), default));
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task ExistingHistoryAppliesToBothComparisonsWithoutBeingExtended()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(8); f.Scrape(73);
        await f.Replicas.CheckScaleAsync("default");
        f.Scrape(0);
        var before = f.Scaler.CaptureHistory(f.Name);
        var preview = await f.Simulation.SimulateAsync(new(f.Name), default);
        Assert.Equal(0, preview.Current.RawTarget); Assert.Equal(8, preview.Current.Target);
        Assert.Contains(preview.Current.Reasons, r => r.Code == "Stabilization");
        Assert.Equal(before.Decisions, f.Scaler.CaptureHistory(f.Name).Decisions);
        Assert.Equal(before.Recommendations, f.Scaler.CaptureHistory(f.Name).Recommendations);
    }

    private static async Task<string> Telemetry(string name)
    {
        using var stream = new MemoryStream(); await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        return string.Join('\n', Encoding.UTF8.GetString(stream.ToArray()).Split('\n').Where(line => line.Contains(name)));
    }
}
