using DotNext;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.Commands;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;

namespace SlimFaas.Streams;

public interface IStreamService
{
    Task<SlimDataPayload> ReadAsync(CancellationToken cancellationToken);
    Task<DurableStreamMutationResult> MutateAsync(DurableStreamMutation mutation, CancellationToken cancellationToken);
    bool CanAccess(HttpContext context, DurableStreamDefinition definition);
    bool CanObserveFunction(HttpContext context, string function);
    bool CanObserveActivity(HttpContext context, NetworkActivityEvent activity);
    IReadOnlyList<StreamSummary> Summaries(HttpContext context, SlimDataPayload payload);
    IReadOnlyList<StreamFunctionSummary> Functions(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition);
    DeploymentInformation? Function(string name);
}

public sealed class StreamService(IDatabaseService database, ISupplier<SlimDataPayload> state,
    IReplicasService replicas, IWebSocketFunctionRepository virtualFunctions, IFunctionAccessPolicy access,
    NetworkActivityTracker activity, ILogger<StreamService> logger) : IStreamService
{
    public DeploymentInformation? Function(string name) =>
        replicas.Deployments.Functions.Concat(virtualFunctions.GetVirtualDeployments())
            .FirstOrDefault(function => function.Deployment == name);

    public async Task<SlimDataPayload> ReadAsync(CancellationToken cancellationToken)
    {
        _ = await database.GetAsync("\0stream:read-barrier").WaitAsync(cancellationToken).ConfigureAwait(false);
        return state.Invoke();
    }

    public bool CanAccess(HttpContext context, DurableStreamDefinition definition) =>
        definition.Visibility == "Public" || access.IsInternalRequest(context);
    public bool CanObserveFunction(HttpContext context, string function)
    {
        if (access.IsInternalRequest(context)) return true;
        if (Function(function) is { } definition) return access.CanAccessFunction(context, definition, "");
        var payload = state.Invoke();
        var subscriptions = DurableStreamStore.Definitions(payload).SelectMany(stream =>
            DurableStreamStore.Subscriptions(payload, stream.Name)).Where(subscription => subscription.Function == function).ToArray();
        var active = subscriptions.Where(subscription => subscription.Active).ToArray();
        return subscriptions.Length > 0 && (active.Length > 0 ? active : subscriptions)
            .All(subscription => subscription.FunctionVisibility == "Public");
    }
    public bool CanObserveActivity(HttpContext context, NetworkActivityEvent activity)
    {
        if (activity.StreamName is null)
            return true;
        var definition = DurableStreamStore.Definition(state.Invoke(), activity.StreamName);
        return (definition is not null ? CanAccess(context, definition) :
                activity.StreamVisibility == "Public" || access.IsInternalRequest(context)) &&
            (activity.StreamFunction is null || CanObserveFunction(context, activity.StreamFunction));
    }

    public async Task<DurableStreamMutationResult> MutateAsync(DurableStreamMutation mutation, CancellationToken cancellationToken)
    {
        mutation.NowTicks = DateTime.UtcNow.Ticks;
        if (string.IsNullOrEmpty(mutation.NewId))
            mutation.NewId = (mutation.Action == DurableStreamAction.Reserve ? "stream_" : "") + Guid.NewGuid().ToString("N");
        DurableStreamMutationResult result;
        var before = DurableStreamStore.Definition(state.Invoke(), mutation.Stream);
        try { result = await database.ExecuteStreamAsync(mutation).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogStreamPersistenceFailure(exception, mutation.Stream, mutation.Action);
            throw;
        }
        var after = DurableStreamStore.Definition(state.Invoke(), mutation.Stream);
        if (result.Status < 300 && before is not null && after is not null && after.Earliest > before.Earliest)
            activity.Record("stream_retention", NetworkActivityTracker.Actors.SlimFaas, "stream:" + mutation.Stream,
                streamName: mutation.Stream, streamVisibility: after.Visibility);
        if (result.Status >= 400)
            logger.LogStreamMutationRejected(mutation.Stream, mutation.Action, result.Status, result.Error);
        if (result.Status < 300 && !result.Unchanged && (mutation.Action != DurableStreamAction.Reserve || result.Delivery is not null) &&
            mutation.Action is DurableStreamAction.Append or DurableStreamAction.Reserve or
            DurableStreamAction.AwaitCallback or DurableStreamAction.Complete or DurableStreamAction.Delete)
        {
            var attempt = result.Delivery ?? DurableStreamStore.Attempt(state.Invoke(), mutation.ElementId);
            string type = mutation.Action switch
            {
                DurableStreamAction.Append => "stream_append",
                DurableStreamAction.Reserve => "stream_delivery",
                DurableStreamAction.Delete => "stream_deleted",
                _ => attempt?.State switch
                {
                    "Acknowledged" => "stream_ack", "WaitingForCallback" => "stream_waiting",
                    "RetryScheduled" => "stream_retry", _ => "stream_failed"
                }
            };
            string actor = "stream:" + mutation.Stream;
            activity.Record(type, mutation.Action == DurableStreamAction.Reserve ? actor :
                attempt?.Function ?? NetworkActivityTracker.Actors.SlimFaas,
                mutation.Action == DurableStreamAction.Reserve ? mutation.Function : actor,
                targetPod: result.Delivery?.Replica, sourcePod: result.Delivery is null ? attempt?.Replica : null,
                correlationId: attempt?.ElementId,
                elementId: attempt?.ElementId, streamName: mutation.Stream, streamFunction: attempt?.Function,
                streamMessageId: result.MessageId ?? attempt?.MessageId, streamVisibility: (after ?? before)?.Visibility);
        }
        return result;
    }

    public IReadOnlyList<StreamSummary> Summaries(HttpContext context, SlimDataPayload payload)
    {
        var now = DateTimeOffset.UtcNow;
        return DurableStreamStore.Definitions(payload).Where(definition => CanAccess(context, definition))
            .OrderBy(definition => definition.Name, StringComparer.Ordinal).Select(definition =>
            {
                var earliest = DurableStreamStore.Messages(payload, definition, definition.Earliest - 1, 1).FirstOrDefault();
                var latest = DurableStreamStore.Messages(payload, definition, definition.Tail - 1, 1).FirstOrDefault();
                return new StreamSummary(definition.Name, definition.Visibility,
                    Math.Max(0, definition.Tail - definition.Earliest + 1),
                    DurableStreamStore.Subscriptions(payload, definition.Name).Count(subscription =>
                        subscription.Active && CanObserveFunction(context, subscription.Function)),
                    earliest is null ? null : Time(earliest.AppendedAtTicks), latest is null ? null : Time(latest.AppendedAtTicks),
                    definition.MaxAgeSeconds, definition.MaxLength, earliest?.Id, latest?.Id, definition.Generation, now, definition.Revision);
            }).ToArray();
    }

    public IReadOnlyList<StreamFunctionSummary> Functions(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition)
    {
        if (!CanAccess(context, definition))
            return [];
        return DurableStreamStore.Subscriptions(payload, definition.Name)
            .Where(subscription => CanObserveFunction(context, subscription.Function))
            .OrderBy(subscription => subscription.Function, StringComparer.Ordinal).Select(subscription =>
            {
                var counters = DurableStreamStore.Counters(payload, definition, subscription);
                return new StreamFunctionSummary(definition.Name, subscription.Function, subscription.Active,
                    subscription.Generation, subscription.StartSequence, subscription.AcknowledgedThrough, subscription.LastAcknowledged,
                    subscription.RetentionGapCount, counters.Lag, counters.PendingCount, counters.WaitingForCallbackCount,
                    counters.RetryScheduledCount, counters.FailedCount, counters.RunnableCount, subscription.LastStatus,
                    DateTimeOffset.UtcNow, definition.Revision);
            }).ToArray();
    }

    internal static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);
}
