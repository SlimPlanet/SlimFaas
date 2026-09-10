namespace SlimFaas.Kubernetes;

internal enum ScalerState
{
    Valid,
    Unavailable,
    Timeout,
    InvalidMetric,
    Stale,
    Misconfigured
}

internal readonly record struct ScalerResult(ScalerState State, double Value = 0, bool IsActive = false);

internal sealed record ScalerContext(
    string Namespace,
    string Function,
    ScaleConfig Configuration,
    ScaleTrigger Trigger,
    long NowUnixSeconds);

// Providers supply observations only; replica policies and writes stay in the engine.
internal interface IScalerProvider
{
    ValueTask<ScalerResult> GetAsync(ScalerContext context, CancellationToken cancellationToken);
}

internal sealed record ScalerTriggerResult(ScaleTrigger Trigger, ScalerResult Result, int Index = -1);

internal sealed record ScalerEvaluation(IReadOnlyList<ScalerTriggerResult> Triggers)
{
    public bool HasActiveExternalSignal => Triggers.Any(t =>
        t.Trigger.Source is not null && t.Result.State == ScalerState.Valid && t.Result.IsActive &&
        double.IsFinite(t.Result.Value) && t.Result.Value > 0 &&
        double.IsFinite(t.Trigger.Threshold) && t.Trigger.Threshold > 0);
}
