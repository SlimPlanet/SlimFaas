using System.Collections.Concurrent;
using Prometheus;

namespace SlimFaas.Kubernetes;

internal static class AutoScalerTelemetry
{
    private static readonly Gauge TriggerValue = Metrics.CreateGauge(
        "slimfaas_autoscaler_trigger_value",
        "Latest value evaluated for an autoscaling trigger.",
        "function",
        "metric",
        "source");

    private static readonly Gauge TriggerValid = Metrics.CreateGauge(
        "slimfaas_autoscaler_trigger_valid",
        "Whether the latest autoscaling trigger evaluation produced usable data.",
        "function",
        "metric",
        "source");

    private static readonly Gauge CurrentReplicas = Metrics.CreateGauge(
        "slimfaas_autoscaler_current_replicas",
        "Current replicas observed by the autoscaler.",
        "function");

    private static readonly Gauge DesiredReplicas = Metrics.CreateGauge(
        "slimfaas_autoscaler_desired_replicas",
        "Latest desired replicas computed by the autoscaler.",
        "function");

    private static readonly Gauge ReadyReplicas = Metrics.CreateGauge(
        "slimfaas_autoscaler_ready_replicas",
        "Ready replicas observed by the autoscaler.",
        "function");

    private static readonly Gauge LastDecision = Metrics.CreateGauge(
        "slimfaas_autoscaler_last_decision",
        "Latest autoscaling decision. The active direction and reason have value one.",
        "function",
        "direction",
        "reason");

    private static readonly ConcurrentDictionary<string, (string Direction, string Reason)> PreviousDecisions =
        new(StringComparer.Ordinal);

    public static void RecordTrigger(
        string key,
        string metricName,
        string source,
        double value,
        bool isValid)
    {
        var metric = string.IsNullOrWhiteSpace(metricName) ? "unnamed" : metricName;
        TriggerValue.WithLabels(key, metric, source).Set(isValid ? value : 0);
        TriggerValid.WithLabels(key, metric, source).Set(isValid ? 1 : 0);
    }

    public static void RecordDecision(
        string key,
        int currentReplicas,
        int desiredReplicas,
        bool hasInvalidTrigger)
    {
        CurrentReplicas.WithLabels(key).Set(currentReplicas);
        DesiredReplicas.WithLabels(key).Set(desiredReplicas);

        var direction = desiredReplicas > currentReplicas
            ? "up"
            : desiredReplicas < currentReplicas
                ? "down"
                : "hold";
        var reason = hasInvalidTrigger && direction == "hold"
            ? "missing_metric"
            : "metric";

        if (PreviousDecisions.TryGetValue(key, out var previous) &&
            (previous.Direction != direction || previous.Reason != reason))
        {
            LastDecision.WithLabels(key, previous.Direction, previous.Reason).Set(0);
        }

        LastDecision.WithLabels(key, direction, reason).Set(1);
        PreviousDecisions[key] = (direction, reason);
    }

    public static void RecordReadyReplicas(string key, int readyReplicas) =>
        ReadyReplicas.WithLabels(key).Set(readyReplicas);
}
