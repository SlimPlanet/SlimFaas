using SlimFaas.Kubernetes;
using SlimFaas.MetricsQuery;

namespace SlimFaas.Tests.Kubernetes;

public sealed class AutoScalerTests
{
    private static PromQlMiniEvaluator CreateEvaluator()
    {
        // Snapshot minimal non vide, suffisant pour les expressions scalaires (ex : "10")
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> SnapshotProvider()
        {
            var metrics = new Dictionary<string, double> { { "dummy_metric", 1.0 } };
            var pod = new Dictionary<string, IReadOnlyDictionary<string, double>>
            {
                { "pod-0", metrics }
            };
            var deployment = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
            {
                { "dummy-deploy", pod }
            };
            var root = new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>
            {
                { 1L, deployment }
            };
            return root;
        }

        return new PromQlMiniEvaluator(SnapshotProvider);
    }

    private static AutoScaler CreateAutoScaler(IAutoScalerStore? store = null)
    {
        var evaluator = CreateEvaluator();
        store ??= new InMemoryAutoScalerStore();
        return new AutoScaler(evaluator, store, logger: null);
    }

    private static ScaleConfig MakeSimpleScaleConfig(
        double metricValue,
        double threshold,
        ScaleMetricType metricType = ScaleMetricType.AverageValue)
    {
        return new ScaleConfig
        {
            ReplicaMax = null,
            Triggers = new List<ScaleTrigger>
            {
                new ScaleTrigger(
                    MetricType: metricType,
                    MetricName: "dummy",
                    Query: metricValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Threshold: threshold
                )
            },
            Behavior = new ScaleBehavior()
        };
    }

    [Fact]
    public void NoScaleConfig_ShouldClampCurrentBetweenMinAndMax()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        var desired = scaler.ComputeDesiredReplicas(
            key,
            scaleConfig: null,
            currentReplicas: 5,
            minReplicas: 2,
            maxReplicas: 10,
            nowUnixSeconds: now);

        Assert.Equal(5, desired);

        var desiredWhenBelowMin = scaler.ComputeDesiredReplicas(
            key,
            scaleConfig: null,
            currentReplicas: 1,
            minReplicas: 3,
            maxReplicas: 10,
            nowUnixSeconds: now);

        Assert.Equal(3, desiredWhenBelowMin);
    }

    [Fact]
    public void EmptyTriggers_ShouldClampCurrentBetweenMinAndMax()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        var cfg = new ScaleConfig
        {
            ReplicaMax = 8,
            Triggers = new List<ScaleTrigger>(),   // vide
            Behavior = new ScaleBehavior()
        };

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 6,
            minReplicas: 2,
            maxReplicas: 8,
            nowUnixSeconds: now);

        Assert.Equal(6, desired);
    }

    [Fact]
    public void SingleTrigger_MetricEqualThreshold_ShouldKeepCurrent()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric = 10, threshold = 10 → ratio = 1 → desired = current
        var cfg = MakeSimpleScaleConfig(metricValue: 10, threshold: 10);

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 5,
            minReplicas: 1,
            maxReplicas: 20,
            nowUnixSeconds: now);

        Assert.Equal(5, desired);
    }

    [Fact]
    public void SingleTrigger_MetricAboveThreshold_ShouldScaleUpWithFormula()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric = 20, threshold = 10 → ratio = 2 → desired = ceil(3 * 2) = 6
        var cfg = MakeSimpleScaleConfig(metricValue: 20, threshold: 10);

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 3,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: now);

        Assert.Equal(6, desired);
    }

    [Fact]
    public void SingleTrigger_MetricBelowThreshold_ShouldScaleDownWithFormula()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric = 5, threshold = 10 → ratio = 0.5 → ceil(10 * 0.5) = 5
        var cfg = MakeSimpleScaleConfig(metricValue: 5, threshold: 10);

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 10,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: now);

        Assert.Equal(5, desired);
    }

    [Fact]
    public void SingleTrigger_ScaleDown_ShouldRespectMinReplicas()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric = 1, threshold = 10 → ratio = 0.1 → ceil(10 * 0.1) = 1
        var cfg = MakeSimpleScaleConfig(metricValue: 1, threshold: 10);

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 10,
            minReplicas: 7,
            maxReplicas: 100,
            nowUnixSeconds: now);

        Assert.Equal(7, desired);
    }

    [Fact]
    public void SingleTrigger_ScaleUp_ShouldRespectReplicaMax()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric très élevé → desired brut très grand, clampé à ReplicaMax
        var cfg = MakeSimpleScaleConfig(metricValue: 100, threshold: 1) with
        {
            ReplicaMax = 10
        };

        // On neutralise les limites de ScaleUp pour ce test
        cfg = cfg with
        {
            Behavior = new ScaleBehavior
            {
                ScaleUp = new ScaleDirectionBehavior
                {
                    StabilizationWindowSeconds = 0,
                    Policies = new List<ScalePolicy>() // aucune policy => pas de limitation
                },
                // ScaleDown par défaut, pas utilisé ici
                ScaleDown = ScaleDirectionBehavior.DefaultScaleDown()
            }
        };

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 3,
            minReplicas: 1,
            maxReplicas: 10,
            nowUnixSeconds: now);

        // desired brut = 300, clampé à ReplicaMax = 10, pas de limitation par policies
        Assert.Equal(10, desired);
    }


    [Fact]
    public void MultipleTriggers_ShouldTakeMaxDesiredAcrossTriggers()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // Trigger1: metric = 10, thr = 10 → ratio = 1 → desired = 3
        // Trigger2: metric = 30, thr = 10 → ratio = 3 → desired = 9
        var cfg = new ScaleConfig
        {
            ReplicaMax = null,
            Triggers = new List<ScaleTrigger>
            {
                new(
                    MetricType: ScaleMetricType.AverageValue,
                    MetricName: "m1",
                    Query: "10",
                    Threshold: 10
                ),
                new(
                    MetricType: ScaleMetricType.AverageValue,
                    MetricName: "m2",
                    Query: "30",
                    Threshold: 10
                )
            },
            Behavior = new ScaleBehavior
            {
                // On neutralise les limites de ScaleUp pour ce test
                ScaleUp = new ScaleDirectionBehavior
                {
                    StabilizationWindowSeconds = 0,
                    Policies = new List<ScalePolicy>()
                },
                // On peut garder le ScaleDown par défaut, il ne sera pas utilisé ici
                ScaleDown = ScaleDirectionBehavior.DefaultScaleDown()
            }
        };

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 3,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: now);

        // max(desiredTrigger1=3, desiredTrigger2=9) = 9
        Assert.Equal(9, desired);
    }


    [Fact]
    public void FromZero_WithPositiveMetric_ShouldScaleFromOne()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // current = 0 → effectiveCurrent = 1
        // metric = 10, thr = 5 → ratio = 2 → desired = ceil(1 * 2) = 2
        var cfg = MakeSimpleScaleConfig(metricValue: 10, threshold: 5);

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 0,
            minReplicas: 0,
            maxReplicas: 100,
            nowUnixSeconds: now);

        Assert.Equal(2, desired);
    }

    [Fact]
    public void ScaleUpPolicies_ShouldLimitIncrease_MaxOfPolicies()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric = 30, thr = 10 → ratio = 3 → desired brut = ceil(10 * 3) = 30
        var cfg = MakeSimpleScaleConfig(metricValue: 30, threshold: 10);

        var scaleUp = new ScaleDirectionBehavior
        {
            StabilizationWindowSeconds = 0,
            Policies =
            {
                new ScalePolicy(ScalePolicyType.Percent, 50, 30), // 50% de 10 = 5
                new ScalePolicy(ScalePolicyType.Pods,    3,  30)  // 3 pods
            }
        };

        cfg = cfg with
        {
            Behavior = new ScaleBehavior
            {
                ScaleUp = scaleUp,
                ScaleDown = ScaleDirectionBehavior.DefaultScaleDown()
            }
        };

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 10,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: now);

        // delta brut = 20, maxIncAllowed = max(5,3) = 5 → 10 + 5 = 15
        Assert.Equal(15, desired);
    }

    [Fact]
    public void ScaleUpPolicies_WithZeroLimits_ShouldBlockIncrease()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric = 30, thr = 10 → desired brut = 30
        var cfg = MakeSimpleScaleConfig(metricValue: 30, threshold: 10);

        var scaleUp = new ScaleDirectionBehavior
        {
            StabilizationWindowSeconds = 0,
            Policies =
            {
                new ScalePolicy(ScalePolicyType.Percent, 0, 30),
                new ScalePolicy(ScalePolicyType.Pods,    0, 30)
            }
        };

        cfg = cfg with
        {
            Behavior = new ScaleBehavior
            {
                ScaleUp = scaleUp,
                ScaleDown = ScaleDirectionBehavior.DefaultScaleDown()
            }
        };

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 10,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: now);

        // Aucun delta autorisé → on reste à 10
        Assert.Equal(10, desired);
    }

    [Fact]
    public void ScaleDownPolicies_ShouldLimitDecrease_MinOfPolicies()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric = 5, thr = 10 → ratio = 0.5 → desired brut = ceil(10 * 0.5) = 5
        var cfg = MakeSimpleScaleConfig(metricValue: 5, threshold: 10);

        var scaleDown = new ScaleDirectionBehavior
        {
            StabilizationWindowSeconds = 0,
            Policies =
            {
                new ScalePolicy(ScalePolicyType.Percent, 50, 30), // 50% de 10 = 5
                new ScalePolicy(ScalePolicyType.Pods,    3,  30)  // 3 pods
            }
        };

        cfg = cfg with
        {
            Behavior = new ScaleBehavior
            {
                ScaleUp = ScaleDirectionBehavior.DefaultScaleUp(),
                ScaleDown = scaleDown
            }
        };

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 10,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: now);

        // delta brut = 5, minDecAllowed = min(5,3) = 3 → 10 - 3 = 7
        Assert.Equal(7, desired);
    }

    [Fact]
    public void ScaleDownStabilization_ShouldKeepHigherReplicaWithinWindow()
    {
        var store = new InMemoryAutoScalerStore();
        var scaler = CreateAutoScaler(store);
        string key = "func";
        long t0 = 1_000;
        long t1 = 1_010;

        var cfg = MakeSimpleScaleConfig(metricValue: 20, threshold: 10);

        // ScaleDown behavior avec fenêtre de 300s
        var scaleDown = new ScaleDirectionBehavior
        {
            StabilizationWindowSeconds = 300,
            Policies = { }
        };

        cfg = cfg with
        {
            Behavior = new ScaleBehavior
            {
                ScaleUp = ScaleDirectionBehavior.DefaultScaleUp(),
                ScaleDown = scaleDown
            }
        };

        // 1er appel : metric = 20, thr = 10 → ratio = 2 → desired = 10
        var first = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 5,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: t0);

        Assert.Equal(10, first);

        // 2ème appel : metric plus faible → ratio = 0.1 → desired brut = 1
        // mais StabilizationWindow → max des recommandations dans la fenêtre → 10
        var cfgLowMetric = MakeSimpleScaleConfig(metricValue: 1, threshold: 10) with
        {
            Behavior = cfg.Behavior
        };

        var second = scaler.ComputeDesiredReplicas(
            key,
            cfgLowMetric,
            currentReplicas: 10,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: t1);

        Assert.Equal(10, second);
    }

    [Fact]
    public void ScaleDownStabilization_ShouldExpireAfterWindow()
    {
        var store = new InMemoryAutoScalerStore();
        var scaler = CreateAutoScaler(store);
        string key = "func";
        long t0 = 1_000;
        long t1 = 1_010;
        long t2 = 1_400; // > t0 + 300

        var scaleDown = new ScaleDirectionBehavior
        {
            StabilizationWindowSeconds = 300,
            Policies = { }
        };

        var cfgHigh = MakeSimpleScaleConfig(metricValue: 20, threshold: 10) with
        {
            Behavior = new ScaleBehavior
            {
                ScaleUp = ScaleDirectionBehavior.DefaultScaleUp(),
                ScaleDown = scaleDown
            }
        };

        var cfgLow = MakeSimpleScaleConfig(metricValue: 1, threshold: 10) with
        {
            Behavior = cfgHigh.Behavior
        };

        // t0 : monte à 10
        var first = scaler.ComputeDesiredReplicas(
            key,
            cfgHigh,
            currentReplicas: 5,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: t0);
        Assert.Equal(10, first);

        // t1 : tentative de descente à 1, bloquée à 10 (stabilisation)
        var second = scaler.ComputeDesiredReplicas(
            key,
            cfgLow,
            currentReplicas: 10,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: t1);
        Assert.Equal(10, second);

        // t2 : fenêtre expirée → on accepte la descente à 1
        var third = scaler.ComputeDesiredReplicas(
            key,
            cfgLow,
            currentReplicas: 10,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: t2);
        Assert.Equal(1, third);
    }

    [Fact]
    public void DeploymentOverload_ShouldUseScaleConfigFromDeployment()
    {
        var scaler = CreateAutoScaler();
        long now = 1_000;

        var cfg = MakeSimpleScaleConfig(metricValue: 20, threshold: 10);

        var deployment = new DeploymentInformation(
            Deployment: "my-func",
            Namespace: "default",
            Pods: new List<PodInformation>(),
            Configuration: new SlimFaasConfiguration(),
            Replicas: 3,
            ReplicasAtStart: 1,
            ReplicasMin: 1,
            TimeoutSecondBeforeSetReplicasMin: 300,
            NumberParallelRequest: 10,
            ReplicasStartAsSoonAsOneFunctionRetrieveARequest: false,
            PodType: PodType.Deployment,
            DependsOn: null,
            Schedule: null,
            SubscribeEvents: null,
            Visibility: FunctionVisibility.Public,
            PathsStartWithVisibility: null,
            ResourceVersion: "",
            EndpointReady: false,
            Trust: FunctionTrust.Trusted,
            Scale: cfg
        );

        var desired = scaler.ComputeDesiredReplicas(deployment, now);

        // metric = 20, thr = 10 → ratio = 2 → desired = ceil(3 * 2) = 6
        Assert.Equal(6, desired);
    }

    [Fact]
    public void TriggersWithEmptyQueryOrNonPositiveThreshold_ShouldBeIgnored()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        var cfg = new ScaleConfig
        {
            Triggers = new List<ScaleTrigger>
            {
                new(
                    MetricType: ScaleMetricType.AverageValue,
                    MetricName: "ignored1",
                    Query: "",
                    Threshold: 10
                ),
                new(
                    MetricType: ScaleMetricType.AverageValue,
                    MetricName: "ignored2",
                    Query: "10",
                    Threshold: 0       // non positif
                )
            }
        };

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 5,
            minReplicas: 2,
            maxReplicas: 10,
            nowUnixSeconds: now);

        // Aucune métrique exploitable → on reste sur current clampé
        Assert.Equal(5, desired);
    }

    [Fact]
    public void ScaleToZeroAllowed_WhenMinReplicasIsZero()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        // metric = 0, thr = 10 → ratio = 0 → desired brut = 0
        var cfg = MakeSimpleScaleConfig(metricValue: 0, threshold: 10);

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 1,
            minReplicas: 0,
            maxReplicas: 100,
            nowUnixSeconds: now);

        Assert.Equal(0, desired);
    }

    [Fact]
    public void ScaleToZeroBlocked_WhenMinReplicasGreaterThanZero()
    {
        var scaler = CreateAutoScaler();
        string key = "func";
        long now = 1_000;

        var cfg = MakeSimpleScaleConfig(metricValue: 0, threshold: 10);

        var desired = scaler.ComputeDesiredReplicas(
            key,
            cfg,
            currentReplicas: 1,
            minReplicas: 1,
            maxReplicas: 100,
            nowUnixSeconds: now);

        // clamp à min=1
        Assert.Equal(1, desired);
    }
}
