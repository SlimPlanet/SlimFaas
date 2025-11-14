using System.Text.Json.Serialization;

namespace SlimFaas.Kubernetes;

public enum ScaleMetricType
{
    AverageValue, // la valeur moyenne par pod visée
    Value         // la valeur totale (somme) visée
}

public enum ScalePolicyType
{
    Percent,
    Pods
}

public record ScaleTrigger(
    ScaleMetricType MetricType = ScaleMetricType.AverageValue,
    string MetricName = "",
    string Query = "",
    double Threshold = 0
);

public record ScalePolicy(
    ScalePolicyType Type = ScalePolicyType.Percent,
    int Value = 100,
    int PeriodSeconds = 15
);

public record ScaleDirectionBehavior
{
    public int StabilizationWindowSeconds { get; init; }
    public IList<ScalePolicy> Policies { get; init; } = new List<ScalePolicy>();

    // Defaults séparés pour ScaleUp / ScaleDown
    public static ScaleDirectionBehavior DefaultScaleUp() => new()
    {
        StabilizationWindowSeconds = 0,
        Policies =
        {
            new ScalePolicy(ScalePolicyType.Percent, 100, 15),
            new ScalePolicy(ScalePolicyType.Pods,    4,   15)
        }
    };

    public static ScaleDirectionBehavior DefaultScaleDown() => new()
    {
        StabilizationWindowSeconds = 300,
        Policies =
        {
            new ScalePolicy(ScalePolicyType.Percent, 100, 15)
        }
    };
}

public record ScaleBehavior
{
    public ScaleDirectionBehavior ScaleUp { get; init; } = ScaleDirectionBehavior.DefaultScaleUp();
    public ScaleDirectionBehavior ScaleDown { get; init; } = ScaleDirectionBehavior.DefaultScaleDown();
}

public record ScaleConfig
{
    // Si l'annotation n'est pas définie -> reste null (comportement demandé)
    public int? ReplicaMax { get; init; } = null;

    // Par défaut, pas de triggers si non fournis
    public IList<ScaleTrigger> Triggers { get; init; } = new List<ScaleTrigger>();

    // Par défaut, comportement = defaults ci-dessus
    public ScaleBehavior Behavior { get; init; } = new();
}



[JsonSerializable(typeof(ScaleConfig))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ScaleConfigSerializerContext : JsonSerializerContext;
