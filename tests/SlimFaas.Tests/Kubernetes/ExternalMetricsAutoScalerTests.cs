using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public sealed class ExternalMetricsAutoScalerTests
{
    private static readonly ScaleBehavior UnlimitedBehavior = new()
    {
        ScaleUp = new ScaleDirectionBehavior { Policies = [] },
        ScaleDown = new ScaleDirectionBehavior { Policies = [] }
    };

    [Fact]
    public void ExternalSource_WakesFromZeroAndAdjustsRunningReplicas()
    {
        const long now = 1_000;
        const string function = "worker";
        const string source = "queue";
        string scope = MetricsSourceScope.ForExternal(function, source);
        var values = new Dictionary<string, double> { [scope] = 2 };
        var health = Healthy(scope, now, "queue_depth");
        var scaler = CreateScaler(values, now, health);
        ScaleConfig config = Config(new ScaleTrigger(
            ScaleMetricType.AverageValue,
            "queue",
            "queue_depth",
            1,
            source));

        Assert.Equal(2, scaler.ComputeDesiredReplicas(function, config, 0, 0, 10, now));

        values[scope] = 4;
        health.RecordSuccess(scope, now + 1, 1_000, ["queue_depth"]);
        Assert.Equal(4, scaler.ComputeDesiredReplicas(function, config, 2, 0, 10, now + 1));
    }

    [Fact]
    public void TriggersUseIsolatedLocalAndExternalScopesAndAggregateByMaximum()
    {
        const long now = 2_000;
        const string function = "worker";
        string queueScope = MetricsSourceScope.ForExternal(function, "queue");
        string brokerScope = MetricsSourceScope.ForExternal(function, "broker");
        var values = new Dictionary<string, double>
        {
            [function] = 1,
            [queueScope] = 3,
            [brokerScope] = 5
        };
        var health = Healthy(queueScope, now, "pressure");
        health.RecordSuccess(brokerScope, now, 1_000, ["pressure"]);
        var scaler = CreateScaler(values, now, health);
        ScaleConfig config = Config(
            new ScaleTrigger(ScaleMetricType.AverageValue, "local", "pressure", 1),
            new ScaleTrigger(ScaleMetricType.AverageValue, "queue", "pressure", 1, "queue"),
            new ScaleTrigger(ScaleMetricType.AverageValue, "broker", "pressure", 1, "broker"));

        Assert.Equal(5, scaler.ComputeDesiredReplicas(function, config, 2, 0, 10, now));
    }

    [Fact]
    public void LocalResidualMetricCannotWakeAFunctionFromZero()
    {
        const long now = 3_000;
        const string function = "worker";
        var scaler = CreateScaler(new Dictionary<string, double> { [function] = 10 }, now);
        ScaleConfig config = Config(
            new ScaleTrigger(ScaleMetricType.AverageValue, "local", "pressure", 1));

        Assert.Equal(0, scaler.ComputeDesiredReplicas(function, config, 0, 0, 10, now));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing")]
    [InlineData("stale")]
    public void InvalidExternalSourceBlocksScaleDown(string invalidState)
    {
        const long now = 4_000;
        const string function = "worker";
        string scope = MetricsSourceScope.ForExternal(function, "queue");
        var health = new ExternalMetricsSourceHealthRegistry();
        if (invalidState == "failed")
        {
            health.RecordSuccess(scope, now, 1_000, ["queue_depth"]);
            health.RecordFailure(scope, now, 1_000);
        }
        else if (invalidState == "missing")
        {
            health.RecordSuccess(scope, now, 1_000, ["another_metric"]);
        }
        else
        {
            health.RecordSuccess(scope, now - 4, 1_000, ["queue_depth"]);
        }

        var scaler = CreateScaler(new Dictionary<string, double> { [scope] = 0 }, now, health);
        ScaleConfig config = Config(new ScaleTrigger(
            ScaleMetricType.AverageValue,
            "queue",
            "queue_depth",
            1,
            "queue"));

        Assert.Equal(4, scaler.ComputeDesiredReplicas(function, config, 4, 0, 10, now));
    }

    [Fact]
    public void InvalidExternalTriggerDoesNotPreventAnotherTriggerFromScalingUp()
    {
        const long now = 5_000;
        const string function = "worker";
        string healthyScope = MetricsSourceScope.ForExternal(function, "healthy");
        var health = Healthy(healthyScope, now, "pressure");
        var scaler = CreateScaler(new Dictionary<string, double> { [healthyScope] = 6 }, now, health);
        ScaleConfig config = Config(
            new ScaleTrigger(ScaleMetricType.AverageValue, "healthy", "pressure", 1, "healthy"),
            new ScaleTrigger(ScaleMetricType.AverageValue, "failed", "pressure", 1, "failed"));

        Assert.Equal(6, scaler.ComputeDesiredReplicas(function, config, 2, 0, 10, now));
    }

    [Fact]
    public void ExternalTriggerHonorsReplicaMaxAndScaleUpPolicy()
    {
        const long now = 6_000;
        const string function = "worker";
        string scope = MetricsSourceScope.ForExternal(function, "queue");
        var health = Healthy(scope, now, "queue_depth");
        var scaler = CreateScaler(new Dictionary<string, double> { [scope] = 100 }, now, health);
        ScaleConfig config = Config(new ScaleTrigger(
            ScaleMetricType.AverageValue,
            "queue",
            "queue_depth",
            1,
            "queue")) with
        {
            ReplicaMax = 8,
            Behavior = new ScaleBehavior
            {
                ScaleUp = new ScaleDirectionBehavior
                {
                    Policies = [new ScalePolicy(ScalePolicyType.Pods, 2, 0)]
                },
                ScaleDown = new ScaleDirectionBehavior { Policies = [] }
            }
        };

        Assert.Equal(5, scaler.ComputeDesiredReplicas(function, config, 3, 0, config.ReplicaMax, now));
    }

    private static ScaleConfig Config(params ScaleTrigger[] triggers) => new()
    {
        Triggers = triggers,
        Behavior = UnlimitedBehavior
    };

    private static ExternalMetricsSourceHealthRegistry Healthy(
        string scope,
        long now,
        params string[] names)
    {
        var health = new ExternalMetricsSourceHealthRegistry();
        health.RecordSuccess(scope, now, 1_000, names);
        return health;
    }

    private static AutoScaler CreateScaler(
        IDictionary<string, double> valuesByScope,
        long timestamp,
        IExternalMetricsSourceHealthRegistry? health = null)
    {
        IReadOnlyDictionary<long, IReadOnlyDictionary<string,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> SnapshotProvider()
        {
            var scopes = valuesByScope.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>)
                    new Dictionary<string, IReadOnlyDictionary<string, double>>
                    {
                        ["target"] = new Dictionary<string, double> { ["pressure"] = pair.Value, ["queue_depth"] = pair.Value }
                    },
                StringComparer.Ordinal);
            return new Dictionary<long, IReadOnlyDictionary<string,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>
            {
                [timestamp] = scopes
            };
        }

        return new AutoScaler(
            new PromQlMiniEvaluator(SnapshotProvider),
            new InMemoryAutoScalerStore(),
            logger: null,
            externalHealthRegistry: health);
    }
}
