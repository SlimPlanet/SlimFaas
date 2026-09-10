using Prometheus;
using System.Collections.Concurrent;

namespace SlimFaas.Kubernetes;

internal static class ExternalScalerTelemetry
{
    private static readonly ConcurrentDictionary<(string Function, string Source, string Metric), ScalerState> Triggers = new();
    private static readonly Gauge SourceAvailable = Metrics.CreateGauge(
        "slimfaas_scaler_source_available", "Whether the latest external scrape succeeded.", "function", "source", "provider");
    private static readonly Gauge LastSuccess = Metrics.CreateGauge(
        "slimfaas_scaler_source_last_success_unixtime", "Last successful external scrape.", "function", "source", "provider");
    private static readonly Counter Scrapes = Metrics.CreateCounter(
        "slimfaas_scaler_source_scrapes_total", "External scrape outcomes.", "function", "source", "provider", "state");
    private static readonly Gauge TriggerValue = Metrics.CreateGauge(
        "slimfaas_scaler_trigger_value", "Latest external trigger value.", "function", "source", "provider", "metric");
    private static readonly Gauge TriggerValid = Metrics.CreateGauge(
        "slimfaas_scaler_trigger_valid", "Whether an external trigger is usable.", "function", "source", "provider", "metric");
    private static readonly Gauge TriggerDesired = Metrics.CreateGauge(
        "slimfaas_scaler_trigger_desired_replicas", "Unconstrained external trigger recommendation.", "function", "source", "provider", "metric");
    private static readonly Gauge TriggerState = Metrics.CreateGauge(
        "slimfaas_scaler_trigger_state", "Latest external trigger state (one active state per trigger).",
        "function", "source", "provider", "metric", "state");

    internal static void RecordSource(ExternalMetricsSource source, ScalerState state, long lastSuccess)
    {
        SourceAvailable.WithLabels(source.Function, source.Name, "prometheus").Set(state == ScalerState.Valid ? 1 : 0);
        LastSuccess.WithLabels(source.Function, source.Name, "prometheus").Set(lastSuccess);
        Scrapes.WithLabels(source.Function, source.Name, "prometheus", state.ToString()).Inc();
    }

    internal static void RecordTrigger(string function, ScaleTrigger trigger, ScalerResult result, double desired)
    {
        var labels = new[] { function, trigger.Source ?? "", "prometheus", trigger.MetricName };
        var key = (function, trigger.Source ?? "", trigger.MetricName);
        if (Triggers.TryGetValue(key, out var previous) && previous != result.State)
            TriggerState.WithLabels(function, trigger.Source ?? "", "prometheus", trigger.MetricName, previous.ToString()).Set(0);
        Triggers[key] = result.State;
        TriggerState.WithLabels(function, trigger.Source ?? "", "prometheus", trigger.MetricName, result.State.ToString()).Set(1);
        var valid = result.State == ScalerState.Valid;
        TriggerValue.WithLabels(labels).Set(valid ? result.Value : 0);
        TriggerValid.WithLabels(labels).Set(valid ? 1 : 0);
        TriggerDesired.WithLabels(labels).Set(valid ? desired : 0);
    }

    internal static void InvalidateSource(ExternalMetricsSource source)
    {
        SourceAvailable.WithLabels(source.Function, source.Name, "prometheus").Set(0);
        foreach (var key in Triggers.Keys)
            if (key.Function == source.Function && key.Source == source.Name)
                RecordTrigger(key.Function, new(MetricName: key.Metric, Source: key.Source),
                    new(ScalerState.Unavailable), 0);
    }

    internal static void RemoveSource(ExternalMetricsSource source)
    {
        SourceAvailable.RemoveLabelled(source.Function, source.Name, "prometheus");
        LastSuccess.RemoveLabelled(source.Function, source.Name, "prometheus");
        foreach (var state in Enum.GetValues<ScalerState>())
            Scrapes.RemoveLabelled(source.Function, source.Name, "prometheus", state.ToString());
    }

    internal static void RetainTriggers(IEnumerable<DeploymentInformation> functions)
    {
        var active = functions.SelectMany(f => (f.Scale?.Triggers ?? []).Where(t => t.Source is not null)
            .Select(t => (f.Deployment, t.Source!, t.MetricName))).ToHashSet();
        foreach (var key in Triggers.Keys)
        {
            if (active.Contains(key) || !Triggers.TryRemove(key, out _)) continue;
            var labels = new[] { key.Function, key.Source, "prometheus", key.Metric };
            TriggerValue.RemoveLabelled(labels);
            TriggerValid.RemoveLabelled(labels);
            TriggerDesired.RemoveLabelled(labels);
            foreach (var state in Enum.GetValues<ScalerState>())
                TriggerState.RemoveLabelled(key.Function, key.Source, "prometheus", key.Metric, state.ToString());
        }
    }
}
