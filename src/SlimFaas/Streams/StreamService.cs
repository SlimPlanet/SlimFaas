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
using ZLinq;

namespace SlimFaas.Streams;

public interface IStreamService
{
    bool FrontEnabled { get; }
    bool CanManage(HttpContext context);
    Task<SlimDataPayload> ReadAsync(CancellationToken cancellationToken);
    Task<DurableStreamMutationResult> MutateAsync(DurableStreamMutation mutation, CancellationToken cancellationToken);
    bool CanAccess(HttpContext context, DurableStreamDefinition definition);
    bool CanObserveFunction(HttpContext context, string function);
    bool CanObserveActivity(HttpContext context, NetworkActivityEvent activity);
    IReadOnlyList<StreamSummary> Summaries(HttpContext context, SlimDataPayload payload);
    IReadOnlyList<StreamSummary> Summaries(HttpContext context, SlimDataPayload payload, string prefix, string? after, int limit) =>
        Summaries(context, payload).Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal) &&
            (after is null || string.CompareOrdinal(item.Name, after) > 0)).Take(limit).ToArray();
    StreamSummary? Summary(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition) =>
        Summaries(context, payload).FirstOrDefault(item => item.Name == definition.Name);
    IReadOnlyList<StreamFunctionSummary> Functions(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition);
    IReadOnlyList<StreamFunctionSummary> Functions(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition, string? after, int limit) =>
        Functions(context, payload, definition).Where(item => after is null || string.CompareOrdinal(item.Function, after) > 0).Take(limit).ToArray();
    StreamFunctionSummary? FunctionSummary(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition, string function) =>
        Functions(context, payload, definition).FirstOrDefault(item => item.Function == function);
    DeploymentInformation? Function(string name);
}

public sealed class StreamService(IDatabaseService database, ISupplier<SlimDataPayload> state,
    IReplicasService replicas, IWebSocketFunctionRepository virtualFunctions, IFunctionAccessPolicy access,
    NetworkActivityTracker activity, ILogger<StreamService> logger,
    IOptions<SlimFaasOptions> options) : IStreamService
{
    public bool FrontEnabled => options.Value.EnableFront && options.Value.Streams.EnableFront;
    public bool CanManage(HttpContext context)
    {
        bool allowed = options.Value.Streams.DefaultVisibility == FunctionVisibility.Public || access.IsInternalRequest(context);
        if (!allowed) logger.LogStreamManagementDenied();
        return allowed;
    }

    public DeploymentInformation? Function(string name)
    {
        var functions = replicas.Deployments.Functions;
        var virtualDeployments = virtualFunctions.GetVirtualDeployments();
        return DeploymentsInformations.FindFunction(functions, name) ??
            DeploymentsInformations.FindFunction(virtualDeployments, name);
    }

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
        var subscriptions = DurableStreamStore.Definitions(payload).AsValueEnumerable().SelectMany(stream =>
            DurableStreamStore.Subscriptions(payload, stream.Name).AsValueEnumerable())
            .Where(subscription => subscription.Function == function).ToArray();
        var active = subscriptions.AsValueEnumerable().Where(static subscription => subscription.Active).ToArray();
        return subscriptions.Length > 0 && (active.Length > 0 ? active : subscriptions)
            .All(subscription => subscription.FunctionVisibility == "Public");
    }
    public bool CanObserveActivity(HttpContext context, NetworkActivityEvent activity)
    {
        if (activity.StreamName is null)
            return true;
        if (!FrontEnabled || !CanManage(context)) return false;
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
                // An accepted completion is authoritative even before this follower's snapshot catches up.
                _ when mutation.Action == DurableStreamAction.AwaitCallback || mutation.StatusCode == 202 => "stream_waiting",
                _ when mutation.StatusCode is >= 200 and < 300 => "stream_ack",
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

    public IReadOnlyList<StreamSummary> Summaries(HttpContext context, SlimDataPayload payload) =>
        Summaries(context, payload, "", null, int.MaxValue);

    public IReadOnlyList<StreamSummary> Summaries(HttpContext context, SlimDataPayload payload, string prefix, string? after, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        return DurableStreamStore.Definitions(payload).AsValueEnumerable().Where(definition =>
                definition.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                (after is null || string.CompareOrdinal(definition.Name, after) > 0) && CanAccess(context, definition))
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal).Take(limit)
            .Select(definition => BuildSummary(context, payload, definition, now)).ToArray();
    }

    public StreamSummary? Summary(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition) =>
        CanAccess(context, definition) ? BuildSummary(context, payload, definition, DateTimeOffset.UtcNow) : null;

    private StreamSummary BuildSummary(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition, DateTimeOffset now)
    {
        var earliest = DurableStreamStore.Messages(payload, definition, definition.Earliest - 1, 1).FirstOrDefault();
        var latest = DurableStreamStore.Messages(payload, definition, definition.Tail - 1, 1).FirstOrDefault();
        return new StreamSummary(definition.Name, definition.Visibility,
            Math.Max(0, definition.Tail - definition.Earliest + 1),
            DurableStreamStore.Subscriptions(payload, definition.Name).Count(subscription =>
                subscription.Active && CanObserveFunction(context, subscription.Function)),
            earliest is null ? null : Time(earliest.AppendedAtTicks), latest is null ? null : Time(latest.AppendedAtTicks),
            definition.MaxAgeSeconds, definition.MaxLength, earliest?.Id, latest?.Id, definition.Generation, now, definition.Revision);
    }

    public IReadOnlyList<StreamFunctionSummary> Functions(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition) =>
        Functions(context, payload, definition, null, int.MaxValue);

    public StreamFunctionSummary? FunctionSummary(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition, string function)
    {
        if (!CanAccess(context, definition) || !CanObserveFunction(context, function))
            return null;
        var subscription = DurableStreamStore.Subscription(payload, definition.Name, function);
        return subscription is null ? null : BuildFunctionSummary(payload, definition, subscription);
    }

    public IReadOnlyList<StreamFunctionSummary> Functions(HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition, string? after, int limit)
    {
        if (!CanAccess(context, definition))
            return [];
        return DurableStreamStore.Subscriptions(payload, definition.Name)
            .AsValueEnumerable()
            .Where(subscription => (after is null || string.CompareOrdinal(subscription.Function, after) > 0) &&
                CanObserveFunction(context, subscription.Function))
            .OrderBy(static subscription => subscription.Function, StringComparer.Ordinal).Take(limit)
            .Select(subscription => BuildFunctionSummary(payload, definition, subscription)).ToArray();
    }

    private static StreamFunctionSummary BuildFunctionSummary(SlimDataPayload payload, DurableStreamDefinition definition, DurableStreamSubscription subscription)
    {
        var counters = DurableStreamStore.Counters(payload, definition, subscription);
        return new StreamFunctionSummary(definition.Name, subscription.Function, subscription.Active,
            subscription.Generation, subscription.StartSequence, subscription.AcknowledgedThrough, subscription.LastAcknowledged,
            subscription.RetentionGapCount, counters.Lag, counters.PendingCount, counters.WaitingForCallbackCount,
            counters.RetryScheduledCount, counters.FailedCount, counters.RunnableCount, subscription.LastStatus,
            DateTimeOffset.UtcNow, definition.Revision);
    }

    internal static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);
}
