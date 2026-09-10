using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Scaling;
using Xunit;

namespace SlimFaas.Tests.Scaling;

public class ScaleDownBudgetTests
{
    private static async Task<ScalingTestFixture> Create(int replicas = 10, ScalePolicy[]? policies = null,
        bool local = false, int stabilization = 0)
    {
        var fixture = new ScalingTestFixture();
        fixture.Config = fixture.Config with
        {
            Behavior = new ScaleBehavior
            {
                ScaleUp = new() { Policies = [] },
                ScaleDown = new()
                {
                    Policies = policies ?? [new(ScalePolicyType.Pods, 1, 10)],
                    StabilizationWindowSeconds = stabilization
                }
            }
        };
        if (local) fixture.Config.Triggers[0] = fixture.Config.Triggers[0] with { Source = null };
        await fixture.SetFunctions(replicas);
        return fixture;
    }

    private static async Task<int> Tick(ScalingTestFixture fixture, int elapsedSeconds, double pending = 0)
    {
        fixture.Time.Now = DateTimeOffset.FromUnixTimeSeconds(1000 + elapsedSeconds);
        fixture.Scrape(pending);
        if (fixture.Config.Triggers[0].Source is null)
            fixture.Metrics.Add(fixture.Time.Now.ToUnixTimeSeconds(), fixture.Name, "pod",
                new Dictionary<string, double> { ["jobs_pending"] = pending });
        await fixture.Replicas.CheckScaleAsync("default");
        return fixture.Replicas.Deployments.Functions[0].Replicas;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenReplicasDecreaseOnePerPeriodUntilZero(bool local)
    {
        var fixture = await Create(local: local);
        for (int elapsed = 0; elapsed <= 100; elapsed++)
        {
            int expected = Math.Max(0, 9 - elapsed / 10);
            Assert.Equal(expected, await Tick(fixture, elapsed));
        }
        Assert.Equal(10, fixture.Scaler.CaptureHistory(fixture.Name).Decisions.Count);
    }

    [Fact]
    public async Task SameSecondAndUnexpiredPeriodCannotRemoveAnotherReplica()
    {
        var fixture = await Create();
        Assert.Equal(9, await Tick(fixture, 0));
        Assert.Equal(9, await Tick(fixture, 0));
        Assert.Equal(9, await Tick(fixture, 9));
        Assert.Equal(8, await Tick(fixture, 10));
        Assert.Equal(8, await Tick(fixture, 11));
    }

    [Fact]
    public async Task PartialRemovalsShareTheirBudgetEvenInTheSameSecond()
    {
        var fixture = await Create(policies: [new(ScalePolicyType.Pods, 3, 10)]);
        Assert.Equal(9, await Tick(fixture, 0, 90));
        Assert.Equal(7, await Tick(fixture, 0));
        Assert.Equal(7, await Tick(fixture, 9));
        Assert.Equal(4, await Tick(fixture, 10));
    }

    [Theory]
    [InlineData(3, 50, 2)]
    [InlineData(1, 50, 1)]
    [InlineData(100, 29, 71)]
    [InlineData(200, 10, 180)]
    [InlineData(3, 100, 0)]
    public async Task PercentUsesAReplicaQuotaWithFloorRounding(int current, int percent, int expected)
    {
        var fixture = await Create(current, [new(ScalePolicyType.Percent, percent, 10)]);
        fixture.Config = fixture.Config with { ReplicaMax = current };
        await fixture.SetFunctions(current);
        Assert.Equal(expected, await Tick(fixture, 0));
        Assert.Equal(expected, await Tick(fixture, 1));
    }

    [Fact]
    public async Task PercentBudgetUsesPeriodStartInsteadOfShrinkingAfterEachPartialRemoval()
    {
        var fixture = await Create(policies: [new(ScalePolicyType.Percent, 50, 10)]);
        Assert.Equal(8, await Tick(fixture, 0, 80));
        Assert.Equal(5, await Tick(fixture, 1));
        Assert.Equal(5, await Tick(fixture, 9));
        // The first removal expired: the period starts at eight, with three removals still counted.
        Assert.Equal(4, await Tick(fixture, 10));
        Assert.Equal(3, await Tick(fixture, 11));
    }

    [Theory]
    [InlineData(ScalePolicyType.Pods, 2)]
    [InlineData(ScalePolicyType.Percent, 20)]
    public async Task ScaleUpDoesNotRefundRemovals(ScalePolicyType type, int value)
    {
        var fixture = await Create(policies: [new(type, value, 10)]);
        Assert.Equal(8, await Tick(fixture, 0));
        Assert.Equal(10, await Tick(fixture, 1, 100));
        Assert.Equal(10, await Tick(fixture, 2));
        Assert.Equal(type == ScalePolicyType.Pods ? 8 : 9, await Tick(fixture, 10));
    }

    [Fact]
    public async Task ExhaustedPolicyStillLimitsOtherPoliciesWithDifferentPeriods()
    {
        var fixture = await Create(policies: [new(ScalePolicyType.Pods, 1, 10), new(ScalePolicyType.Pods, 2, 30)]);
        Assert.Equal(9, await Tick(fixture, 0));
        Assert.Equal(9, await Tick(fixture, 1));
        Assert.Equal(8, await Tick(fixture, 10));
        Assert.Equal(8, await Tick(fixture, 20));
        Assert.Equal(7, await Tick(fixture, 30));
        Assert.Equal(6, await Tick(fixture, 40));
    }

    [Fact]
    public async Task RoundedZeroPercentageParticipatesInConservativeMinimum()
    {
        var fixture = await Create(1, [new(ScalePolicyType.Percent, 50, 0), new(ScalePolicyType.Pods, 1, 10)]);
        Assert.Equal(1, await Tick(fixture, 0));
        Assert.Empty(fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
    }

    [Fact]
    public async Task ZeroPeriodLimitsEachDecisionWithoutATemporalBudget()
    {
        var fixture = await Create(3, [new(ScalePolicyType.Pods, 1, 0)]);
        Assert.Equal(2, await Tick(fixture, 0));
        Assert.Equal(1, await Tick(fixture, 0));
        Assert.Equal(0, await Tick(fixture, 0));
    }

    [Fact]
    public async Task LongHighDemandPlateauRefreshesStabilizationBeforeGradualReduction()
    {
        var fixture = await Create(stabilization: 20);
        for (int elapsed = 0; elapsed <= 120; elapsed++)
            Assert.Equal(10, await Tick(fixture, elapsed, 100));
        for (int elapsed = 121; elapsed <= 140; elapsed++)
            Assert.Equal(10, await Tick(fixture, elapsed));
        Assert.Empty(fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
        Assert.Equal(9, await Tick(fixture, 141));
        Assert.Equal(9, await Tick(fixture, 150));
        Assert.Equal(8, await Tick(fixture, 151));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivityFloorOnlyConsumesAcceptedReduction(bool local)
    {
        var fixture = await Create(policies: [new(ScalePolicyType.Pods, 3, 10)], local: local);
        var function = fixture.Replicas.Deployments.Functions[0] with { ReplicasAtStart = 9 };
        await fixture.SetFunctions(functions: [function]);
        fixture.Http.SetTickLastCall(fixture.Name, fixture.Time.Now.UtcDateTime.Ticks);
        Assert.Equal(9, await Tick(fixture, 0));
        Assert.Equal(9, await Tick(fixture, 1));
        var change = Assert.Single(fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
        Assert.Equal(10, change.PreviousReplicas);
        Assert.Equal(9, change.DesiredReplicas);
        fixture.Http.SetTickLastCall(fixture.Name, 0);
        Assert.Equal(7, await Tick(fixture, 2));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedWriteLeavesBudgetAvailable(bool local, bool throws)
    {
        var fixture = await Create(local: local);
        if (throws)
            fixture.Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ThrowsAsync(new InvalidOperationException());
        else
            fixture.Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ReturnsAsync((ReplicaRequest?)null);
        if (throws) await Assert.ThrowsAsync<InvalidOperationException>(() => Tick(fixture, 0));
        else Assert.Equal(10, await Tick(fixture, 0));
        Assert.Empty(fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
        fixture.Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ReturnsAsync((ReplicaRequest r) => r);
        Assert.Equal(9, await Tick(fixture, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedCountAndAcceptanceTimeDetermineBudget(bool local)
    {
        var fixture = await Create(policies: [new(ScalePolicyType.Pods, 3, 10)], local: local);
        fixture.Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ReturnsAsync((ReplicaRequest r) =>
        {
            fixture.Time.Now = fixture.Time.Now.AddSeconds(5);
            return r with { Replicas = 9 };
        });
        Assert.Equal(9, await Tick(fixture, 0));
        var change = Assert.Single(fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
        Assert.Equal(1005, change.TimestampUnixSeconds);
        Assert.Equal(10, change.PreviousReplicas);
        Assert.Equal(9, change.DesiredReplicas);
        fixture.Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ReturnsAsync((ReplicaRequest r) => r);
        Assert.Equal(7, await Tick(fixture, 6));
        Assert.Equal(7, await Tick(fixture, 14));
        Assert.Equal(6, await Tick(fixture, 15));
    }

    [Fact]
    public async Task UnchangedAcceptedCountConsumesNoBudget()
    {
        var fixture = await Create();
        fixture.Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ReturnsAsync((ReplicaRequest r) => r with { Replicas = 10 });
        Assert.Equal(10, await Tick(fixture, 0));
        Assert.Empty(fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
        fixture.Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ReturnsAsync((ReplicaRequest r) => r);
        Assert.Equal(9, await Tick(fixture, 1));
    }

    [Fact]
    public async Task AnotherFunctionsFailurePreservesSuccessfulChangeAndReplicaCount()
    {
        var fixture = await Create();
        var other = fixture.Replicas.Deployments.Functions[0] with { Deployment = fixture.Name + "-failed" };
        await fixture.SetFunctions(functions: [fixture.Replicas.Deployments.Functions[0], other]);
        fixture.Scrape(0, other.Deployment);
        fixture.Kubernetes.Setup(k => k.ScaleAsync(It.Is<ReplicaRequest>(r => r.Deployment == other.Deployment)))
            .ThrowsAsync(new InvalidOperationException());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tick(fixture, 0));
        Assert.Equal(9, fixture.Replicas.Deployments.Functions[0].Replicas);
        Assert.Equal(10, fixture.Replicas.Deployments.Functions[1].Replicas);
        Assert.Single(fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
        Assert.Empty(fixture.Scaler.CaptureHistory(other.Deployment).Decisions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedScaleUpConsumesNoBudget(bool local)
    {
        var fixture = await Create(local: local);
        var function = fixture.Replicas.Deployments.Functions[0] with
        {
            Pods = [new("blocked", false, false, "127.0.0.1", fixture.Name) { StartFailureReason = "Unschedulable" }]
        };
        await fixture.SetFunctions(functions: [function]);
        Assert.Equal(10, await Tick(fixture, 0, 200));
        Assert.Empty(fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
    }

    [Fact]
    public async Task InvalidTriggerPreventsReductionEvenAfterBudgetExpires()
    {
        var fixture = await Create();
        Assert.Equal(9, await Tick(fixture, 0));
        fixture.Config.Triggers.Add(new(Query: "missing_metric", Threshold: 1));
        Assert.Equal(9, await Tick(fixture, 10));
        fixture.Config.Triggers.RemoveAt(1);
        Assert.Equal(8, await Tick(fixture, 10));
    }

    [Fact]
    public async Task PreviewUsesAcceptedChangesWithoutConsumingOrRefreshingHistory()
    {
        var fixture = await Create();
        Assert.Equal(9, await Tick(fixture, 0));
        foreach (int elapsed in new[] { 1, 10 })
        {
            fixture.Time.Now = DateTimeOffset.FromUnixTimeSeconds(1000 + elapsed);
            fixture.Scrape(0);
            var before = fixture.Scaler.CaptureHistory(fixture.Name);
            var preview = await fixture.Simulation.SimulateAsync(new(fixture.Name), default);
            Assert.Equal(elapsed == 1 ? 9 : 8, preview.Current.Target);
            Assert.Equal(preview.Current.Target, preview.Simulated.Target);
            Assert.Equal(before.Decisions, fixture.Scaler.CaptureHistory(fixture.Name).Decisions);
            Assert.Equal(before.Recommendations, fixture.Scaler.CaptureHistory(fixture.Name).Recommendations);
            Assert.Equal(preview.Current.Target, await Tick(fixture, elapsed));
        }
    }
}
