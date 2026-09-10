using System.Text.Json.Serialization;
using SlimFaas.Kubernetes;

namespace SlimFaas.Scaling;

public sealed record ScalingTriggerDiagnostic(int Index, string Metric, string? Source, string Query,
    string MetricType, double? Threshold, string State, double? Value, int? RawTarget, bool Simulated = false,
    string? Detail = null);

public sealed record MetricsScalingDecision(int? RawTarget, int Recommendation, int PolicyTarget,
    int StabilizedTarget, int Target, bool HasInvalidTriggers, IReadOnlyList<ScalingTriggerDiagnostic> Triggers);

public sealed record ScalingReason(string Code, string Message);
public sealed record ScalingSourceDiagnostic(string Name, string State, long? LastSuccessMs, int IntervalMs);

public sealed record ScalingDecision(string Function, long TimestampMs, int CurrentReplicas, int ReadyReplicas,
    int? RawTarget, int? PolicyTarget, int? StabilizedTarget, int Target, string Action,
    string Application, int? AcceptedReplicas, IReadOnlyList<ScalingReason> Reasons,
    IReadOnlyList<ScalingTriggerDiagnostic> Triggers, IReadOnlyList<ScalingSourceDiagnostic> Sources,
    long? LastHttpActivityMs, long? LastScheduleActivityMs, double InactivityRemainingSeconds,
    bool DependenciesReady, bool DependencyDemand);

public sealed record ScalingEvent(long Id, long TimestampMs, int CurrentReplicas, int Target,
    int? RawTarget, string Action, string Application, string[] Reasons, string[] Signals);

public sealed record ScalingState(string Session, string Status, long ServerTimeMs, string Function,
    ScalingDecision? Decision, ScaleConfig? Configuration, int ObservedReadyReplicas, int ObservedRequestedReplicas,
    IReadOnlyList<ScalingEvent> Events, bool Truncated, int RefreshIntervalMs = 1000);

// Sources/URLs are never accepted from the browser. Overrides refer to configured trigger indices.
public sealed record ScalingTriggerOverride(int Index, string Query, string? Source, ScaleMetricType MetricType,
    double Threshold, double? Value = null);
public sealed record ScalingSimulationRequest(string Function, int? CurrentReplicas = null,
    int? ReplicaMax = null, bool ClearReplicaMax = false, bool? ScaleFromZero = null,
    ScaleBehavior? Behavior = null, IReadOnlyList<ScalingTriggerOverride>? Triggers = null);
public sealed record ScalingSimulationResponse(long CapturedAtMs, ScalingDecision Current,
    ScalingDecision Simulated, string[] Limitations);
public sealed record ScalingError(string Error);

internal sealed record ScalingHistory(IReadOnlyList<AutoScaleSample> Decisions,
    IReadOnlyList<AutoScaleSample> Recommendations);
internal sealed record ScalingEnvironment(DeploymentsInformations Deployments, IReadOnlyDictionary<string, long> HttpTicks,
    bool TurnOnByDefault, DateTime NowUtc);
internal sealed record ScalingFunctionContext(DeploymentInformation Function, DateTime NowUtc,
    long LastHttpTicks, long? LastScheduleTicks, long EffectiveActivityTicks, double TimeoutSeconds,
    bool DependencyDemand, bool DependenciesReady, string? InfrastructureFailure)
{
    public long NowSeconds => new DateTimeOffset(NowUtc).ToUnixTimeSeconds();
    public double InactivityRemainingSeconds =>
        (EffectiveActivityTicks - NowUtc.Ticks) / (double)TimeSpan.TicksPerSecond + TimeoutSeconds;
    public bool Inactive => (TimeSpan.FromTicks(EffectiveActivityTicks) + TimeSpan.FromSeconds(TimeoutSeconds))
        < TimeSpan.FromTicks(NowUtc.Ticks) && !DependencyDemand;
}

[JsonSerializable(typeof(ScalingState))]
[JsonSerializable(typeof(ScalingSimulationRequest))]
[JsonSerializable(typeof(ScalingSimulationResponse))]
[JsonSerializable(typeof(ScalingError))]
[JsonSerializable(typeof(ScalingDecision))]
[JsonSerializable(typeof(ScalingEvent))]
public partial class ScalingJsonContext : JsonSerializerContext;
