using SlimFaas.Scaling;

namespace SlimFaas.Kubernetes;

public sealed class AutoScaler
{
    private readonly IScalerProvider _provider;
    private readonly IAutoScalerStore _store;
    private readonly InMemoryAutoScalerStore _recommendationStore = new();
    private readonly ILogger<AutoScaler>? _logger;
    private readonly object _historyLock = new();

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
        for (int index = 0; index < config.Triggers.Count; index++)
        {
            var trigger = config.Triggers[index];
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
            results.Add(new(trigger, result, index));
        }
        return new(results);
    }

    internal int ComputeDesiredReplicas(DeploymentInformation deployment, long nowUnixSeconds,
        ScalerEvaluation evaluation, bool recordDecision = true)
    {
        return ComputeDecision(deployment, nowUnixSeconds, evaluation, recordDecision).Target;
    }

    internal void RecordAppliedDecision(string function, long nowUnixSeconds, int replicas)
    {
        lock (_historyLock) _store.AddSample(function, nowUnixSeconds, replicas);
    }

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

    internal ScalingHistory CaptureHistory(string key)
    {
        lock (_historyLock)
            return new(_store.GetSamples(key, long.MinValue).ToArray(),
                _recommendationStore.GetSamples(key, long.MinValue).ToArray());
    }

    internal MetricsScalingDecision ComputeDecision(DeploymentInformation deployment, long now,
        ScalerEvaluation evaluation, bool recordDecision = true)
    {
        AutoScalerTelemetry.RecordReadyReplicas(deployment.Deployment, deployment.Pods.Count(p => p.Ready == true));
        return ComputeDecision(deployment.Deployment, deployment.Scale, deployment.Replicas,
            deployment.ReplicasMin, deployment.Scale?.ReplicaMax, now, evaluation, recordDecision);
    }

    private int ComputeDesiredReplicas(string key, ScaleConfig? config, int current, int min, int? max,
        long now, ScalerEvaluation evaluation, bool recordDecision)
        => ComputeDecision(key, config, current, min, max, now, evaluation, recordDecision).Target;

    private MetricsScalingDecision ComputeDecision(string key, ScaleConfig? config, int current, int min, int? max,
        long now, ScalerEvaluation evaluation, bool recordDecision)
    {
        lock (_historyLock)
        {
            var result = MetricsScalingCalculator.Calculate(config, current, min, max, now, evaluation, CaptureHistory(key));
            if (config is null || config.Triggers.Count == 0) return result;
            _recommendationStore.AddSample(key, now, result.Recommendation);
            if (recordDecision && result.Target != Math.Max(0, current)) _store.AddSample(key, now, result.Target);
            foreach (var trigger in result.Triggers)
            {
                var configuredTrigger = config.Triggers[trigger.Index];
                bool valid = trigger.State == nameof(ScalerState.Valid);
                if (configuredTrigger.Source is null)
                    AutoScalerTelemetry.RecordTrigger(key, configuredTrigger.MetricName, trigger.Value ?? 0, valid);
                else
                    ExternalScalerTelemetry.RecordTrigger(key, configuredTrigger,
                        new ScalerResult(Enum.Parse<ScalerState>(trigger.State), trigger.Value ?? 0, trigger.Value > 0),
                        trigger.RawTarget ?? 0);
            }
            AutoScalerTelemetry.RecordDecision(key, Math.Max(0, current), result.Target, result.HasInvalidTriggers);
            return result;
        }
    }
}
