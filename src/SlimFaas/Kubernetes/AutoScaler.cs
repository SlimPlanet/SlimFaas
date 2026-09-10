namespace SlimFaas.Kubernetes;

public sealed class AutoScaler
{
    private readonly IScalerProvider _provider;
    private readonly IAutoScalerStore _store;
    private readonly InMemoryAutoScalerStore _recommendationStore = new();
    private readonly ILogger<AutoScaler>? _logger;
    private readonly record struct TriggerComputation(int DesiredReplicas, bool HasInvalidTrigger);

    internal AutoScaler(IScalerProvider provider, IAutoScalerStore store, ILogger<AutoScaler>? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    internal async ValueTask<ScalerEvaluation> EvaluateAsync(DeploymentInformation deployment,
        long nowUnixSeconds, bool externalOnly = false, CancellationToken cancellationToken = default)
        => await EvaluateAsync(deployment.Namespace, deployment.Deployment, deployment.Scale,
            nowUnixSeconds, externalOnly, cancellationToken);

    private async ValueTask<ScalerEvaluation> EvaluateAsync(string ns, string function, ScaleConfig? config,
        long now, bool externalOnly = false, CancellationToken cancellationToken = default)
    {
        var results = new List<ScalerTriggerResult>();
        if (config is null) return new(results);
        foreach (var trigger in config.Triggers)
        {
            if (externalOnly && trigger.Source is null) continue;
            ScalerResult result;
            try
            {
                result = await _provider.GetAsync(new(ns, function, config, trigger, now), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { result = new(ScalerState.Timeout); }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Scaling provider failed for {Function}", function);
                result = new(ScalerState.Unavailable);
            }
            results.Add(new(trigger, result));
        }
        return new(results);
    }

    internal int ComputeDesiredReplicas(DeploymentInformation deployment, long nowUnixSeconds,
        ScalerEvaluation evaluation, bool recordDecision = true)
    {
        AutoScalerTelemetry.RecordReadyReplicas(deployment.Deployment,
            deployment.Pods.Count(p => p.Ready == true));
        return ComputeDesiredReplicas(deployment.Deployment, deployment.Scale, deployment.Replicas,
            deployment.ReplicasMin, deployment.Scale?.ReplicaMax, nowUnixSeconds, evaluation, recordDecision);
    }

    internal void RecordAppliedDecision(string function, long nowUnixSeconds, int replicas)
        => _store.AddSample(function, nowUnixSeconds, replicas);

    public int ComputeDesiredReplicas(DeploymentInformation deployment, long nowUnixSeconds)
    {
        if (deployment is null) throw new ArgumentNullException(nameof(deployment));

        var evaluation = EvaluateAsync(deployment, nowUnixSeconds).AsTask().GetAwaiter().GetResult();
        return ComputeDesiredReplicas(deployment, nowUnixSeconds, evaluation);
    }

    public int ComputeDesiredReplicas(
    string key,
    ScaleConfig? scaleConfig,
    int currentReplicas,
    int minReplicas,
    int? maxReplicas,
    long nowUnixSeconds)
        => ComputeDesiredReplicas(key, scaleConfig, currentReplicas, minReplicas, maxReplicas,
            nowUnixSeconds, EvaluateAsync("", key, scaleConfig, nowUnixSeconds).AsTask().GetAwaiter().GetResult(), true);

    private int ComputeDesiredReplicas(string key, ScaleConfig? scaleConfig, int currentReplicas,
        int minReplicas, int? maxReplicas, long nowUnixSeconds, ScalerEvaluation evaluation, bool recordDecision)
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
    var triggerComputation = ComputeFromTriggers(
        key,
        scaleConfig,
        currentReplicas,
        minReplicas,
        maxReplicas,
        evaluation);

    var desired = triggerComputation.DesiredReplicas;
    _recommendationStore.AddSample(key, nowUnixSeconds, desired);

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
    if (recordDecision && desired != currentReplicas)
    {
        _store.AddSample(key, nowUnixSeconds, desired);
    }

    AutoScalerTelemetry.RecordDecision(
        key,
        currentReplicas,
        desired,
        triggerComputation.HasInvalidTrigger);

    return desired;
}


    private TriggerComputation ComputeFromTriggers(string key, ScaleConfig config, int currentReplicas,
        int minReplicas, int? maxReplicas, ScalerEvaluation evaluation)
    {
        int? maxDesired = null;
        var hasInvalidTrigger = false;
        foreach (var item in evaluation.Triggers)
        {
            var trigger = item.Trigger;
            var result = item.Result;
            if (result.State != ScalerState.Valid || !double.IsFinite(result.Value) || result.Value < 0 ||
                !double.IsFinite(trigger.Threshold) || trigger.Threshold <= 0)
            {
                hasInvalidTrigger = true;
                if (trigger.Source is null)
                    AutoScalerTelemetry.RecordTrigger(key, trigger.MetricName, 0, isValid: false);
                else
                    ExternalScalerTelemetry.RecordTrigger(key, trigger,
                        result with { State = result.State == ScalerState.Valid ? ScalerState.InvalidMetric : result.State }, 0);
                continue;
            }

            var ratio = result.Value / trigger.Threshold;
            var rawDesired = currentReplicas == 0 && !result.IsActive ? 0 :
                trigger.MetricType == ScaleMetricType.AverageValue ? ratio : Math.Max(1, currentReplicas) * ratio;
            // Saturate before conversion: a very large finite metric must never wrap to zero.
            var desired = (int)Math.Min(int.MaxValue, Math.Ceiling(rawDesired));
            if (trigger.Source is null)
                AutoScalerTelemetry.RecordTrigger(key, trigger.MetricName, result.Value, isValid: true);
            else
                ExternalScalerTelemetry.RecordTrigger(key, trigger, result, desired);
            if (maxReplicas.HasValue) desired = Math.Min(desired, maxReplicas.Value);
            desired = Math.Max(desired, minReplicas);
            maxDesired = Math.Max(maxDesired ?? desired, desired);
        }

        if (maxDesired is null)
            return new(Clamp(currentReplicas, minReplicas, maxReplicas), true);
        var recommendation = hasInvalidTrigger ? Math.Max(currentReplicas, maxDesired.Value) : maxDesired.Value;
        return new(recommendation, hasInvalidTrigger);
    }

    private static int Clamp(int value, int min, int? max)
    {
        if (value < min)
            value = min;
        if (max.HasValue && value > max.Value)
            value = max.Value;
        return value;
    }

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
        var samples = _store.GetSamples(key, fromTs);

        int remainingForPolicy;

        if (samples.Count == 0)
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
        var samples = (isScaleUp ? _store : _recommendationStore)
            .GetSamples(key, fromTs);
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
