using SlimFaas.Kubernetes;

namespace SlimFaas.Scaling;

// Pure calculation: all histories are inputs; no collectors, telemetry or replica writers.
internal static class MetricsScalingCalculator
{
    internal static MetricsScalingDecision Calculate(ScaleConfig? config, int current, int min, int? max,
        long now, ScalerEvaluation evaluation, ScalingHistory history)
    {
        current = Math.Max(0, current);
        min = Math.Max(0, min);
        if (config is null || config.Triggers.Count == 0)
        {
            int target = Clamp(current, min, max);
            return new(null, target, target, target, target, false, []);
        }
        var triggers = new List<ScalingTriggerDiagnostic>();
        int? raw = null, constrained = null;
        bool invalid = false;
        for (int i = 0; i < evaluation.Triggers.Count; i++)
        {
            var item = evaluation.Triggers[i];
            var trigger = item.Trigger;
            var result = item.Result;
            var diagnostic = DescribeTrigger(trigger, result, item.Index >= 0 ? item.Index : i, current);
            if (diagnostic.RawTarget is { } target)
            {
                raw = Math.Max(raw ?? target, target);
                int bounded = Math.Max(min, max.HasValue ? Math.Min(target, max.Value) : target);
                constrained = Math.Max(constrained ?? bounded, bounded);
            }
            else invalid = true;
            triggers.Add(diagnostic);
        }
        invalid |= constrained is null;
        int recommendation = constrained is null ? Clamp(current, min, max) :
            invalid ? Math.Max(current, constrained.Value) : constrained.Value;
        // The runtime adds the current recommendation before applying stabilization, retaining 1024 samples.
        var recommendations = history.Recommendations.TakeLast(1023)
            .Append(new AutoScaleSample(now, recommendation)).ToArray();
        var behavior = config.Behavior ?? new();
        int policy = recommendation, stabilized = recommendation;
        if (recommendation > current)
        {
            policy = ApplyScaleUpPolicies(history.Decisions, behavior.ScaleUp, current, recommendation, now);
            stabilized = ApplyStabilizationWindow(history.Decisions, recommendations,
                behavior.ScaleUp.StabilizationWindowSeconds, policy, true, now);
        }
        else if (recommendation < current)
        {
            policy = ApplyScaleDownPolicies(history.Decisions, behavior.ScaleDown, current, recommendation, now);
            stabilized = ApplyStabilizationWindow(history.Decisions, recommendations,
                behavior.ScaleDown.StabilizationWindowSeconds, policy, false, now);
        }
        return new(raw, recommendation, policy, stabilized, Clamp(stabilized, min, max), invalid, triggers);
    }

    internal static ScalingTriggerDiagnostic DescribeTrigger(ScaleTrigger trigger, ScalerResult result, int index, int current)
    {
        bool valid = result.State == ScalerState.Valid && double.IsFinite(result.Value) && result.Value >= 0
            && double.IsFinite(trigger.Threshold) && trigger.Threshold > 0;
        int? target = null;
        if (valid)
        {
            double ratio = result.Value / trigger.Threshold;
            double desired = current == 0 && !result.IsActive ? 0 :
                trigger.MetricType == ScaleMetricType.AverageValue ? ratio : Math.Max(1, current) * ratio;
            target = (int)Math.Min(int.MaxValue, Math.Ceiling(desired));
        }
        return new(index, trigger.MetricName, trigger.Source, trigger.Query, trigger.MetricType.ToString(),
            double.IsFinite(trigger.Threshold) ? trigger.Threshold : null,
            (valid ? ScalerState.Valid : result.State == ScalerState.Valid ? ScalerState.InvalidMetric : result.State).ToString(),
            valid ? result.Value : null, target);
    }

    private static int Clamp(int value, int min, int? max)
    {
        if (value < min)
            value = min;
        if (max.HasValue && value > max.Value)
            value = max.Value;
        return value;
    }

    internal static int ApplyScaleUpPolicies(
    IReadOnlyList<AutoScaleSample> decisions,
    ScaleDirectionBehavior behavior,
    int currentReplicas,
    int desired,
    long nowUnixSeconds)
{
    if (behavior.Policies is null || behavior.Policies.Count == 0)
        return desired;

    if (desired <= currentReplicas)
        return desired;

    var requestedDelta = desired - currentReplicas;
    var maxAllowedDelta = 0;

    foreach (var policy in behavior.Policies)
    {
        if (policy.Value <= 0)
            continue;

        var baseDelta = ComputeDelta(policy, currentReplicas);
        if (baseDelta <= 0)
            continue;

        // Pas de contrainte temporelle => on applique juste la limite "classique"
        if (policy.PeriodSeconds <= 0)
        {
            if (baseDelta > maxAllowedDelta)
                maxAllowedDelta = baseDelta;
            continue;
        }

        // Avec PeriodSeconds : on ne veut PAS plusieurs scale-up successifs
        // dans la même fenêtre. Chaque "sample" représente déjà une décision
        // de scale (up ou down). Pour Pods, on considère qu'une décision
        // a consommé toute la "Value" pour la fenêtre.
        var fromTs = nowUnixSeconds - policy.PeriodSeconds;
        var samples = decisions.Where(s => s.TimestampUnixSeconds >= fromTs).ToArray();

        int remainingForPolicy;

        if (samples.Length == 0)
        {
            // Aucun scale récent dans cette fenêtre => on peut utiliser la full Value.
            remainingForPolicy = policy.Value;
        }
        else
        {
            // Au moins un scale (up ou down) récent => on considère que la "Value"
            // est déjà consommée pour cette fenêtre.
            remainingForPolicy = 0;
        }

        if (remainingForPolicy <= 0)
            continue;

        var allowedForPolicy = Math.Min(baseDelta, remainingForPolicy);
        if (allowedForPolicy > maxAllowedDelta)
            maxAllowedDelta = allowedForPolicy;
    }

    if (maxAllowedDelta <= 0)
        return currentReplicas;

    var finalDelta = Math.Min(requestedDelta, maxAllowedDelta);
    return currentReplicas + finalDelta;
}


    /// <summary>
    /// Scale DOWN : applique Value + PeriodSeconds de manière conservative.
    /// </summary>
    internal static int ApplyScaleDownPolicies(
        IReadOnlyList<AutoScaleSample> decisions,
        ScaleDirectionBehavior behavior,
        int currentReplicas,
        int desired,
        long nowUnixSeconds)
    {
        if (behavior.Policies is null || behavior.Policies.Count == 0)
            return desired;

        if (desired >= currentReplicas)
            return desired;

        var requestedDelta = currentReplicas - desired;
        int? minAllowedDelta = null;

        foreach (var policy in behavior.Policies)
        {
            if (policy.Value <= 0)
                continue;

            var baseDelta = ComputeDelta(policy, currentReplicas);
            if (baseDelta <= 0)
                continue;

            if (policy.PeriodSeconds > 0)
            {
                var fromTs = nowUnixSeconds - policy.PeriodSeconds;
                var samples = decisions.Where(s => s.TimestampUnixSeconds >= fromTs).ToArray();

                // baseline = max(desired) dans la fenêtre => point le plus haut
                var baseline = currentReplicas;
                if (samples.Length > 0)
                {
                    baseline = samples[0].DesiredReplicas;
                    for (var i = 1; i < samples.Length; i++)
                    {
                        if (samples[i].DesiredReplicas > baseline)
                            baseline = samples[i].DesiredReplicas;
                    }
                }

                var alreadyDown = Math.Max(0, baseline - currentReplicas);
                var remainingDown = Math.Max(0, policy.Value - alreadyDown);
                if (remainingDown <= 0)
                    continue;

                baseDelta = Math.Min(baseDelta, remainingDown);
                if (baseDelta <= 0)
                    continue;
            }

            if (minAllowedDelta is null || baseDelta < minAllowedDelta.Value)
                minAllowedDelta = baseDelta;
        }

        if (minAllowedDelta is null)
            return currentReplicas;

        var finalDelta = Math.Min(requestedDelta, minAllowedDelta.Value);
        return currentReplicas - finalDelta;
    }

    private static int ComputeDelta(ScalePolicy policy, int currentReplicas)
    {
        if (policy.Value <= 0)
            return 0;

        return policy.Type switch
        {
            ScalePolicyType.Pods    => policy.Value,
            ScalePolicyType.Percent => currentReplicas <= 0
                ? 0
                : (int)Math.Floor(currentReplicas * (policy.Value / 100.0)),
            _ => 0
        };
    }

    private static int ApplyStabilizationWindow(
        IReadOnlyList<AutoScaleSample> decisions,
        IReadOnlyList<AutoScaleSample> recommendations,
        int stabilizationWindowSeconds,
        int desired,
        bool isScaleUp,
        long nowUnixSeconds)
    {
        if (stabilizationWindowSeconds <= 0)
            return desired;

        var fromTs = nowUnixSeconds - stabilizationWindowSeconds;
        var samples = (isScaleUp ? decisions : recommendations).Where(s => s.TimestampUnixSeconds >= fromTs).ToArray();
        if (samples.Length == 0)
            return desired;

        if (isScaleUp)
        {
            // Option 2 : scale UP stabilisé
            // On ne laisse pas la nouvelle recommandation dépasser
            // le max des recommandations récentes dans la fenêtre.
            // => Ne peut que réduire "desired", jamais l'augmenter.

            var maxRecent = samples[0].DesiredReplicas;
            for (var i = 1; i < samples.Length; i++)
            {
                var v = samples[i].DesiredReplicas;
                if (v > maxRecent)
                    maxRecent = v;
            }

            // Si le nouveau desired est plus agressif que tout ce qu'on a
            // recommandé récemment, on le rabaisse à maxRecent.
            if (desired > maxRecent)
                return maxRecent;

            return desired;
        }

        // Scale DOWN : comportement conservateur type HPA :
        // on ne descend pas plus bas que la plus grande recommandation récente.
        var maxDesired = desired;
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i].DesiredReplicas;
            if (v > maxDesired)
                maxDesired = v;
        }

        return maxDesired;
    }

}
