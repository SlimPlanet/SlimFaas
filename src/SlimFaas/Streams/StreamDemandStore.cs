using System.Collections.Immutable;
using SlimData;
using SlimData.Commands;
using SlimFaas.Kubernetes;

namespace SlimFaas.Streams;

public sealed class StreamDemandStore
{
    private Func<SlimDataPayload>? _read;
    internal void SetReader(Func<SlimDataPayload> reader) => Volatile.Write(ref _read, reader);
    internal IReadOnlyDictionary<string, long> Capture()
    {
        var reader = Volatile.Read(ref _read);
        if (reader is null) return ImmutableDictionary<string, long>.Empty;
        var payload = reader();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var definition in DurableStreamStore.Definitions(payload))
            foreach (var subscription in DurableStreamStore.Subscriptions(payload, definition.Name))
            {
                long work = subscription.Active ? DurableStreamStore.Counters(payload, definition, subscription).RunnableCount :
                        DurableStreamStore.Deliveries(payload, definition.Name, subscription.Function)
                            .Count(item => item.State is "Running" or "WaitingForCallback");
                if (subscription.Active || work > 0)
                    result[subscription.Function] = result.GetValueOrDefault(subscription.Function) + work;
            }
        return result;
    }
}

internal static class StreamScaling
{
    internal static (DeploymentInformation Function, ScalerEvaluation? Evaluation) Include(
        DeploymentInformation function, ScalerEvaluation? evaluation, IReadOnlyDictionary<string, long>? demand)
    {
        if (demand is null || !demand.TryGetValue(function.Deployment, out long work))
            return (function, evaluation);
        var config = function.Scale ?? new ScaleConfig();
        var trigger = new ScaleTrigger(ScaleMetricType.Value, "stream_work", "", function.NumberParallelRequestPerPod);
        var augmented = config with { Triggers = config.Triggers.Append(trigger).ToArray() };
        return (function with { Scale = augmented }, new ScalerEvaluation((evaluation?.Triggers ?? [])
            .Append(new ScalerTriggerResult(trigger, new ScalerResult(ScalerState.Valid, work, work > 0), config.Triggers.Count)).ToArray()));
    }
}
