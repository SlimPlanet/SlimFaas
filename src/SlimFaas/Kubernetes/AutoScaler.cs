using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SlimFaas.Kubernetes;
using SlimFaas.MetricsQuery;

namespace SlimFaas.Scaling;

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

    /// <summary>
    /// Calcul du desiredReplicas à partir d'un DeploymentInformation SlimFaas.
    /// </summary>
    public int ComputeDesiredReplicas(DeploymentInformation deployment, long nowUnixSeconds)
    {
        if (deployment is null) throw new ArgumentNullException(nameof(deployment));

        var scale = deployment.Scale;
        var current = deployment.Replicas;
        var min = deployment.ReplicasMin;
        int? max = scale?.ReplicaMax;

        // On utilise le nom de deployment comme clé logique dans le store
        return ComputeDesiredReplicas(
            deployment.Deployment,
            scale,
            current,
            min,
            max,
            nowUnixSeconds);
    }

    /// <summary>
    /// Calcul du desiredReplicas avec paramètres explicites.
    /// </summary>
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

        // Pas de config de scale → on clamp juste sur min / max
        if (scaleConfig is null || scaleConfig.Triggers.Count == 0)
        {
            return Clamp(currentReplicas, minReplicas, maxReplicas);
        }

        // 1. Calcul brut depuis les triggers (PromQL + formule HPA)
        var rawDesired = ComputeFromTriggers(scaleConfig, currentReplicas, minReplicas, maxReplicas, nowUnixSeconds);

        // 2. On stocke la recommandation brute pour la fenêtre de stabilisation
        _store.AddSample(key, nowUnixSeconds, rawDesired);

        var desired = rawDesired;
        if (desired == currentReplicas)
            return desired; // rien à faire

        var behavior = scaleConfig.Behavior ?? new ScaleBehavior();

        // 3. Application des policies + fenêtres de stabilisation
        if (desired > currentReplicas)
        {
            desired = ApplyScaleUpPolicies(behavior.ScaleUp, currentReplicas, desired);
            desired = ApplyStabilizationWindow(
                key,
                behavior.ScaleUp.StabilizationWindowSeconds,
                desired,
                isScaleUp: true,
                nowUnixSeconds);
        }
        else
        {
            desired = ApplyScaleDownPolicies(behavior.ScaleDown, currentReplicas, desired);
            desired = ApplyStabilizationWindow(
                key,
                behavior.ScaleDown.StabilizationWindowSeconds,
                desired,
                isScaleUp: false,
                nowUnixSeconds);
        }

        // 4. Clamp final sur min / max
        desired = Clamp(desired, minReplicas, maxReplicas);

        // Autoriser scale-to-zero uniquement si minReplicas == 0
        if (desired <= 0 && minReplicas == 0)
            return 0;

        return desired;
    }

    /// <summary>
    /// Applique la logique "max des triggers" + formule HPA/KEDA :
    /// desired = ceil(currentReplicas * (currentMetric / target)).
    /// </summary>
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
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error while evaluating PromQL query '{Query}'", trigger.Query);
                continue;
            }

            if (double.IsNaN(metricValue) || double.IsInfinity(metricValue))
                continue;

            if (metricValue < 0)
                continue;

            // Formule officielle HPA:
            // desiredReplicas = ceil(currentReplicas * (currentMetricValue / desiredMetricValue))
            //
            // Cas particulier currentReplicas == 0 :
            // on utilise un "current" effectif à 1 pour ne pas rester bloqué à 0
            // lorsque la métrique est > 0 (comportement proche KEDA "activation").
            var effectiveCurrent = currentReplicas == 0 ? 1 : currentReplicas;
            var ratio = metricValue / trigger.Threshold;
            var desiredForTriggerDouble = effectiveCurrent * ratio;
            var desiredForTrigger = (int)Math.Ceiling(desiredForTriggerDouble);

            if (desiredForTrigger < 0)
                desiredForTrigger = 0;

            // Clamp par min / max à ce stade pour éviter les valeurs délirantes
            if (maxReplicas.HasValue && desiredForTrigger > maxReplicas.Value)
                desiredForTrigger = maxReplicas.Value;

            if (desiredForTrigger < minReplicas)
                desiredForTrigger = minReplicas;

            if (maxDesired is null || desiredForTrigger > maxDesired.Value)
                maxDesired = desiredForTrigger;
        }

        if (maxDesired is null)
        {
            // Aucune métrique exploitable → on reste sur la valeur courante clampée.
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
    /// Applique les policies de ScaleUp (Percent / Pods).
    /// Comportement par défaut : selectPolicy = Max (comme HPA/KEDA).
    /// </summary>
    private static int ApplyScaleUpPolicies(
        ScaleDirectionBehavior behavior,
        int currentReplicas,
        int desired)
    {
        if (behavior.Policies is null || behavior.Policies.Count == 0)
            return desired;

        if (desired <= currentReplicas)
            return desired;

        var delta = desired - currentReplicas;
        var maxIncAllowed = 0;

        foreach (var policy in behavior.Policies)
        {
            var allowed = ComputeDelta(policy, currentReplicas);
            if (allowed > maxIncAllowed)
                maxIncAllowed = allowed;
        }

        if (maxIncAllowed <= 0)
            return currentReplicas; // scale up bloqué par les policies

        if (delta > maxIncAllowed)
            return currentReplicas + maxIncAllowed;

        return desired;
    }

    /// <summary>
    /// Applique les policies de ScaleDown.
    /// Comportement par défaut : selectPolicy = Min (comme recommandé pour HPA).
    /// </summary>
    private static int ApplyScaleDownPolicies(
        ScaleDirectionBehavior behavior,
        int currentReplicas,
        int desired)
    {
        if (behavior.Policies is null || behavior.Policies.Count == 0)
            return desired;

        if (desired >= currentReplicas)
            return desired;

        var delta = currentReplicas - desired;

        var minDecAllowed = int.MaxValue;
        var any = false;

        foreach (var policy in behavior.Policies)
        {
            var allowed = ComputeDelta(policy, currentReplicas);
            if (allowed <= 0)
                continue;

            if (!any || allowed < minDecAllowed)
            {
                minDecAllowed = allowed;
                any = true;
            }
        }

        if (!any)
            return desired; // aucune policy efficace → pas de limitation

        if (delta > minDecAllowed)
            return currentReplicas - minDecAllowed;

        return desired;
    }

    /// <summary>
    /// Calcul du delta autorisé par une policy (en nombre de pods).
    /// </summary>
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

    /// <summary>
    /// Applique la fenêtre de stabilisation KEDA/HPA en s'appuyant sur l'historique du store.
    /// Approche : rolling max des desiredReplicas récents pour éviter le flapping.
    /// </summary>
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

        // KEDA/HPA utilisent un "rolling max" des recommandations passées
        // dans la fenêtre pour éviter de supprimer des pods / osciller.
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
