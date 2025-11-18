namespace SlimFaas.Kubernetes;

public sealed class AutoScaler
{
    private readonly PromQlMiniEvaluator _evaluator;
    private readonly IAutoScalerStore _store;
    private readonly ILogger<AutoScaler>? _logger;

    public AutoScaler(
        PromQlMiniEvaluator evaluator,
        IAutoScalerStore store,
        ILogger<AutoScaler>? logger = null)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    public int ComputeDesiredReplicas(DeploymentInformation deployment, long nowUnixSeconds)
    {
        if (deployment is null) throw new ArgumentNullException(nameof(deployment));

        var scale = deployment.Scale;
        var current = deployment.Replicas;
        var min = deployment.ReplicasMin;
        int? max = scale?.ReplicaMax;

        return ComputeDesiredReplicas(
            deployment.Deployment,
            scale,
            current,
            min,
            max,
            nowUnixSeconds);
    }

    public int ComputeDesiredReplicas(
    string key,
    ScaleConfig? scaleConfig,
    int currentReplicas,
    int minReplicas,
    int? maxReplicas,
    long nowUnixSeconds)
{
    if (currentReplicas < 0) currentReplicas = 0;
    if (minReplicas < 0) minReplicas = 0;

    // Pas de config => clamp simple
    if (scaleConfig is null || scaleConfig.Triggers.Count == 0)
    {
        var clamped = Clamp(currentReplicas, minReplicas, maxReplicas);
        // Ici tu peux choisir de ne PAS stocker, ce n'est pas critique pour les policies
        return clamped;
    }

    // 1. Calcul brut via triggers (PromQL + formule HPA)
    var rawDesired = ComputeFromTriggers(
        scaleConfig,
        currentReplicas,
        minReplicas,
        maxReplicas,
        nowUnixSeconds);

    var desired = rawDesired;

    var behavior = scaleConfig.Behavior ?? new ScaleBehavior();

    // 2. Policies + Stabilization (UP / DOWN)
    if (desired > currentReplicas)
    {
        desired = ApplyScaleUpPolicies(
            key,
            behavior.ScaleUp,
            currentReplicas,
            desired,
            nowUnixSeconds);

        desired = ApplyStabilizationWindow(
            key,
            behavior.ScaleUp.StabilizationWindowSeconds,
            desired,
            isScaleUp: true,
            nowUnixSeconds);
    }
    else if (desired < currentReplicas)
    {
        desired = ApplyScaleDownPolicies(
            key,
            behavior.ScaleDown,
            currentReplicas,
            desired,
            nowUnixSeconds);

        desired = ApplyStabilizationWindow(
            key,
            behavior.ScaleDown.StabilizationWindowSeconds,
            desired,
            isScaleUp: false,
            nowUnixSeconds);
    }
    // else desired == currentReplicas : pas de scale, on ne touche pas à l’historique

    // 3. Clamp final
    desired = Clamp(desired, minReplicas, maxReplicas);

    // Scale-to-zero seulement si minReplicas == 0
    if (desired <= 0 && minReplicas == 0)
        desired = 0;

    // 4. On enregistre UNIQUEMENT quand on change réellement la cible
    if (desired != currentReplicas)
    {
        _store.AddSample(key, nowUnixSeconds, desired);
    }

    return desired;
}


    private int ComputeFromTriggers(
        ScaleConfig config,
        int currentReplicas,
        int minReplicas,
        int? maxReplicas,
        long nowUnixSeconds)
    {
        double? maxDesired = null;

        foreach (var trigger in config.Triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger.Query))
                continue;

            if (trigger.Threshold <= 0)
                continue;

            double metricValue;
            try
            {
                metricValue = _evaluator.Evaluate(trigger.Query, nowUnixSeconds);
            }
            catch (InvalidOperationException ex)
            {
                _logger?.LogWarning(ex,
                    "InvalidOperationException while evaluating PromQL query '{Query}'",
                    trigger.Query);
                continue;
            }
            catch (ArgumentException ex)
            {
                _logger?.LogWarning(ex,
                    "ArgumentException while evaluating PromQL query '{Query}'",
                    trigger.Query);
                continue;
            }

            if (double.IsNaN(metricValue) || double.IsInfinity(metricValue))
                continue;

            if (metricValue < 0)
                continue;

            var effectiveCurrent = currentReplicas == 0 ? 1 : currentReplicas;
            var ratio = metricValue / trigger.Threshold;
            var desiredForTriggerDouble = effectiveCurrent * ratio;
            var desiredForTrigger = (int)Math.Ceiling(desiredForTriggerDouble);

            if (desiredForTrigger < 0)
                desiredForTrigger = 0;

            if (maxReplicas.HasValue && desiredForTrigger > maxReplicas.Value)
                desiredForTrigger = maxReplicas.Value;

            if (desiredForTrigger < minReplicas)
                desiredForTrigger = minReplicas;

            if (maxDesired is null || desiredForTrigger > maxDesired.Value)
                maxDesired = desiredForTrigger;
        }

        if (maxDesired is null)
        {
            return Clamp(currentReplicas, minReplicas, maxReplicas);
        }

        return (int)maxDesired.Value;
    }

    private static int Clamp(int value, int min, int? max)
    {
        if (value < min)
            value = min;
        if (max.HasValue && value > max.Value)
            value = max.Value;
        return value;
    }

    /// <summary>
    /// Scale UP : applique Value + PeriodSeconds en utilisant l'historique.
    /// </summary>
    private int ApplyScaleUpPolicies(
        string key,
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

            // Pas de contrainte temporelle => on applique la limite "classique"
            if (policy.PeriodSeconds <= 0)
            {
                if (baseDelta > maxAllowedDelta)
                    maxAllowedDelta = baseDelta;
                continue;
            }

            var fromTs = nowUnixSeconds - policy.PeriodSeconds;
            var samples = _store.GetSamples(key, fromTs);

            // baseline = min(desired) dans la fenêtre => point de départ du scale-up
            var baseline = currentReplicas;
            if (samples.Count > 0)
            {
                baseline = samples[0].DesiredReplicas;
                for (var i = 1; i < samples.Count; i++)
                {
                    if (samples[i].DesiredReplicas < baseline)
                        baseline = samples[i].DesiredReplicas;
                }
            }

            var alreadyUp = Math.Max(0, currentReplicas - baseline);
            var remainingUp = Math.Max(0, policy.Value - alreadyUp);
            if (remainingUp <= 0)
                continue;

            var allowedForPolicy = Math.Min(baseDelta, remainingUp);
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
    private int ApplyScaleDownPolicies(
        string key,
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
                var samples = _store.GetSamples(key, fromTs);

                // baseline = max(desired) dans la fenêtre => point le plus haut
                var baseline = currentReplicas;
                if (samples.Count > 0)
                {
                    baseline = samples[0].DesiredReplicas;
                    for (var i = 1; i < samples.Count; i++)
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

    private int ApplyStabilizationWindow(
        string key,
        int stabilizationWindowSeconds,
        int desired,
        bool isScaleUp,
        long nowUnixSeconds)
    {
        if (stabilizationWindowSeconds <= 0)
            return desired;

        var fromTs = nowUnixSeconds - stabilizationWindowSeconds;
        var samples = _store.GetSamples(key, fromTs);
        if (samples.Count == 0)
            return desired;

        if (isScaleUp)
        {
            // Option 2 : scale UP stabilisé
            // On ne laisse pas la nouvelle recommandation dépasser
            // le max des recommandations récentes dans la fenêtre.
            // => Ne peut que réduire "desired", jamais l'augmenter.

            var maxRecent = samples[0].DesiredReplicas;
            for (var i = 1; i < samples.Count; i++)
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
        for (var i = 0; i < samples.Count; i++)
        {
            var v = samples[i].DesiredReplicas;
            if (v > maxDesired)
                maxDesired = v;
        }

        return maxDesired;
    }

}
