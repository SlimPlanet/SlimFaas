using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Scaling;

public sealed class ScheduledReplicaMinimumTests
{
    [Fact]
    public async Task HttpActivityRestoresActiveMinimumFromOneAndExpiresBackToOne()
    {
        var fixture = new ScalingTestFixture();
        fixture.Time.Now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(1));
        var metadata = FunctionMetadataParser.Parse(new Dictionary<string, string>
        {
            ["SlimFaas/ReplicasAtStart"] = "2",
            ["SlimFaas/ReplicasMin"] = "1",
            ["SlimFaas/TimeoutSecondBeforeSetReplicasMin"] = "3600",
            ["SlimFaas/Schedule"] = """
                {"TimeZoneID":"Europe/Paris","Default":{"WakeUp":["08:00"],"ScaleDownTimeout":[
                {"Time":"09:00","Value":43200},{"Time":"19:00","Value":3600}]}}
                """
        }, fixture.Name);
        await fixture.SetFunctions(functions: [new(fixture.Name, "default", [], new(), 1,
            metadata.ReplicasAtStart, metadata.ReplicasMin, metadata.TimeoutSecondBeforeSetReplicasMin,
            Schedule: metadata.Schedule)]);
        fixture.Http.SetTickLastCall(fixture.Name, fixture.Time.Now.UtcTicks);

        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(2, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);

        fixture.Time.Now = fixture.Time.Now.AddHours(10);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(1, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);
    }

    private static readonly ScheduleConfig ParisSchedule = new()
    {
        TimeZoneID = "Europe/Paris",
        Default = new() { ScaleDownTimeout =
        [
            new() { Time = "19:00", Value = 3600, MinIdleReplicas = 0 },
            new() { Time = "09:00", Value = 43200, MinActiveReplicas = 3 }
        ] }
    };

    [Theory]
    [InlineData(8, 59, 2, 0, 3600)]
    [InlineData(9, 0, 3, 1, 43200)]
    [InlineData(18, 59, 3, 1, 43200)]
    [InlineData(19, 0, 2, 0, 3600)]
    [InlineData(0, 0, 2, 0, 3600)]
    public async Task ScheduleAppliesLocalWindowToLiveAndPreview(int hour, int minute, int active, int idle, int timeout)
    {
        var fixture = new ScalingTestFixture();
        fixture.Time.Now = new DateTimeOffset(2026, 11, 25, hour, minute, 0, TimeSpan.FromHours(1));
        var function = new DeploymentInformation(fixture.Name, "default", [], new(), 1, 2, 1, Schedule: ParisSchedule);
        await fixture.SetFunctions(functions: [function]);
        // Changing a threshold alone does not wake an idle function.
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(idle, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);

        fixture.Http.SetTickLastCall(fixture.Name, fixture.Time.Now.UtcTicks);
        var preview = await fixture.Simulation.SimulateAsync(new(fixture.Name), default);
        Assert.Equal(active, preview.Current.Target);
        Assert.Equal(timeout, preview.Current.InactivityRemainingSeconds);

        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(active, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);
    }

    [Fact]
    public async Task WakeUpAloneRestoresTheScheduledMinimumUsingTheDefaultTimeout()
    {
        var fixture = new ScalingTestFixture();
        fixture.Time.Now = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.FromHours(1));
        var schedule = new ScheduleConfig { TimeZoneID = "Europe/Paris", Default = new()
        {
            WakeUp = ["09:00"], ScaleDownTimeout = [new() { Time = "09:00", MinActiveReplicas = 3 }]
        } };
        await fixture.SetFunctions(functions: [new(fixture.Name, "default", [], new(), 1, 2, 1, 60, Schedule: schedule)]);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(3, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);
        fixture.Time.Now = fixture.Time.Now.AddSeconds(61);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(1, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);
    }

    [Fact]
    public async Task LoweringActiveMinimumLeavesAdditionalLiveReplicasUntilIdle()
    {
        var fixture = new ScalingTestFixture();
        fixture.Time.Now = new DateTimeOffset(2026, 11, 25, 18, 59, 0, TimeSpan.FromHours(1));
        var function = new DeploymentInformation(fixture.Name, "default", [], new(), 3, 2, 1, Schedule: ParisSchedule);
        await fixture.SetFunctions(functions: [function]);
        fixture.Http.SetTickLastCall(fixture.Name, fixture.Time.Now.UtcTicks);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(1);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(3, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);

        fixture.Time.Now = fixture.Time.Now.AddHours(1);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(0, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);
    }

    [Fact]
    public async Task WaitsForDependencyScheduledActiveMinimum()
    {
        var fixture = new ScalingTestFixture();
        fixture.Time.Now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(1));
        var dependency = new DeploymentInformation("database", "default",
            [new("db-1", true, true, "", "database"), new("db-2", true, true, "", "database")],
            new(), 2, 2, 1, Schedule: ParisSchedule);
        var function = new DeploymentInformation(fixture.Name, "default", [], new(), 1, 2, 1, DependsOn: ["database"]);
        fixture.Http.SetTickLastCall(fixture.Name, fixture.Time.Now.UtcTicks);
        await fixture.SetFunctions(functions: [function, dependency]);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(1, fixture.Replicas.Deployments.Functions[0].Replicas);
        Assert.Equal(3, fixture.Replicas.Deployments.Functions[1].Replicas);

        await fixture.SetFunctions(functions: [function, dependency with
        {
            Replicas = 3, Pods = [.. dependency.Pods, new("db-3", true, true, "", "database")]
        }]);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(2, fixture.Replicas.Deployments.Functions[0].Replicas);
    }

    [Fact]
    public async Task HigherMetricRecommendationWinsDuringHttpWakeFromIdle()
    {
        var fixture = new ScalingTestFixture();
        fixture.Time.Now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(1));
        await fixture.SetFunctions(functions: [new(fixture.Name, "default", [], new(), 1, 2, 1,
            Scale: fixture.Config, Schedule: ParisSchedule)]);
        fixture.Http.SetTickLastCall(fixture.Name, fixture.Time.Now.UtcTicks);
        fixture.Scrape(100);
        var preview = await fixture.Simulation.SimulateAsync(new(fixture.Name), default);
        Assert.True(preview.Current.Target > 3);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(preview.Current.Target, Assert.Single(fixture.Replicas.Deployments.Functions).Replicas);
    }

    [Fact]
    public void LocalMidnightAndDstUseThePreviousLocalCalendarDay()
    {
        var function = new DeploymentInformation("clock", "default", [], new(), 1, Schedule: new()
        {
            TimeZoneID = "Europe/Paris", Default = new()
            {
                WakeUp = ["03:30"], ScaleDownTimeout = [new() { Time = "00:15", Value = 10 }, new() { Time = "23:45", Value = 20 }]
            }
        });
        // 00:30 local is still the previous date in UTC.
        Assert.Equal(10, ReplicasService.GetTimeoutSecondBeforeSetReplicasMin(function,
            new DateTime(2026, 6, 11, 22, 30, 0, DateTimeKind.Utc)));
        // Before the Sunday DST transition, Saturday's 03:30 was 02:30 UTC.
        var now = new DateTime(2026, 3, 29, 0, 45, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 3, 28, 2, 30, 0, DateTimeKind.Utc).Ticks,
            ReplicasService.GetLastTicksFromSchedule(function, now));
    }
}
