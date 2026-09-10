using SlimFaas.Kubernetes;

namespace SlimFaas.Scaling;

internal static class ScalingDecisionCalculator
{
    internal static bool ExternalWake(ScalingFunctionContext context, ScalerEvaluation? evaluation) =>
        context.Function.Replicas == 0 && context.Function.Scale?.ScaleFromZero == true &&
        evaluation?.HasActiveExternalSignal == true;

    internal static bool NeedsMetrics(ScalingFunctionContext context, ScalerEvaluation? evaluation) =>
        evaluation is not null && (context.Function.Replicas > 0 ||
            (ExternalWake(context, evaluation) && context.DependenciesReady));

    internal static ScalingDecision Calculate(ScalingFunctionContext context, ScalerEvaluation? evaluation,
        MetricsScalingDecision? metrics, IReadOnlyList<ScalingSourceDiagnostic>? sources = null)
    {
        var function = context.Function;
        int current = function.Replicas, desired = current;
        bool wake = ExternalWake(context, evaluation);
        var reasons = new List<ScalingReason>();
        if (context.Inactive)
        {
            desired = metrics is not null ? Math.Max(function.ReplicasMin, metrics.Target) : function.ReplicasMin;
            reasons.Add(new("InactivityElapsed", "The HTTP/schedule inactivity timeout has elapsed."));
        }
        else if ((current == 0 || current < function.ReplicasMin) && context.DependenciesReady)
        {
            desired = function.ReplicasAtStart;
            reasons.Add(new("ActivityWake", "HTTP, schedule or dependency demand requests ReplicasAtStart."));
        }
        else if (metrics is not null)
        {
            desired = Math.Max(metrics.Target, function.ReplicasAtStart);
            reasons.Add(new("ActivityFloor", "Active HTTP, schedule or dependency demand keeps the ReplicasAtStart floor."));
        }
        else if (!context.Inactive)
            reasons.Add(new("InactivityPending", "The inactivity timeout or dependency demand prevents sleep."));

        if (wake)
        {
            if (!context.DependenciesReady) desired = current;
            else if (metrics is not null) desired = Math.Max(desired, metrics.Target);
            reasons.Add(new("ExternalWake", "A valid external signal requests wake-up."));
        }
        if (current == 0 && function.Scale?.ScaleFromZero == true && function.Scale.ReplicaMax is { } maximum)
            desired = Math.Min(desired, maximum);
        if (current == 0 && function.Scale?.Triggers.Any(t => t.Source is not null) == true && !function.Scale.ScaleFromZero)
            reasons.Add(new("ExternalWakeDisabled", "ScaleFromZero is disabled; external triggers cannot wake this function."));
        if (!context.DependenciesReady)
            reasons.Add(new("DependenciesNotReady", "A required dependency is not ready."));
        if (metrics?.HasInvalidTriggers == true)
            reasons.Add(new("InvalidSignal", "An unavailable or invalid trigger prevents metrics-based reduction."));
        if (metrics?.RawTarget is { } raw && raw != metrics.Recommendation)
            reasons.Add(new("BoundsOrInvalidSignal", "Replica bounds or invalid-signal protection adjusted the raw target."));
        if (metrics is not null && metrics.PolicyTarget != metrics.Recommendation)
            reasons.Add(new("PolicyLimited", "Scaling policies or their period budget adjusted the target."));
        if (metrics is not null && metrics.StabilizedTarget != metrics.PolicyTarget)
            reasons.Add(new("Stabilization", "Recent scaling history holds the target within the stabilization window."));
        if (desired > current && context.InfrastructureFailure is not null)
        {
            desired = current;
            reasons.Add(new("InfrastructureBlocked", "Scale-up is blocked by a reported infrastructure failure."));
        }

        // Preserve every configured trigger, including those intentionally not evaluated at zero.
        var triggers = (function.Scale?.Triggers ?? []).Select((trigger, index) =>
            metrics?.Triggers.FirstOrDefault(t => t.Index == index) ?? Observation(trigger, index, current,
                evaluation?.Triggers.FirstOrDefault(t => t.Index == index)?.Result)).ToArray();
        return new(function.Deployment, new DateTimeOffset(context.NowUtc).ToUnixTimeMilliseconds(), current,
            function.Pods.Count(p => p.Ready == true), metrics?.RawTarget ?? triggers.Select(t => t.RawTarget).Max(), metrics?.PolicyTarget,
            metrics?.StabilizedTarget, desired, desired > current ? "ScaleUp" : desired < current ? "ScaleDown" : "Hold",
            desired == current ? "NotRequired" : "Pending", null, reasons, triggers, sources ?? [],
            ToMilliseconds(context.LastHttpTicks), ToMilliseconds(context.LastScheduleTicks ?? 0),
            Math.Max(0, context.InactivityRemainingSeconds), context.DependenciesReady, context.DependencyDemand);
    }

    private static ScalingTriggerDiagnostic Observation(ScaleTrigger trigger, int index, int current, ScalerResult? result) =>
        result is { } observation ? MetricsScalingCalculator.DescribeTrigger(trigger, observation, index, current) : new(index, trigger.MetricName, trigger.Source, trigger.Query, trigger.MetricType.ToString(),
            double.IsFinite(trigger.Threshold) ? trigger.Threshold : null,
            result?.State.ToString() ?? "NotEvaluated",
            result is { State: ScalerState.Valid } r && double.IsFinite(r.Value) ? r.Value : null, null);

    private static long? ToMilliseconds(long ticks) => ticks > 0
        ? new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null;
}
