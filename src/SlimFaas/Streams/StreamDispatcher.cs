using System.Globalization;
using System.Text.Json;
using DotNext;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.Commands;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Database;
using SlimFaas.WebSocket;

namespace SlimFaas.Streams;

public sealed class StreamDispatcher(IStreamService service, ISupplier<SlimDataPayload> state,
    IReplicasService replicas, IMasterService master, ISlimDataStatus status, ISendClient sender,
    WebSocketConnectionRegistry registry, IOptions<SlimFaasOptions> options, INamespaceProvider namespaceProvider,
    ILogger<StreamDispatcher> logger, StreamDemandStore demand) : BackgroundService
{
    private readonly Dictionary<string, string> _configuration = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _requests = new(StringComparer.Ordinal);
    private readonly string _owner = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await status.WaitForReadyAsync().ConfigureAwait(false);
        demand.SetReader(state.Invoke);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                try
                {
                    foreach (string key in _requests.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToArray())
                    {
                        var request = _requests[key];
                        _requests.Remove(key);
                        await request.ConfigureAwait(false);
                    }
                    if (master.IsMaster)
                        await ConfigureAsync(stoppingToken).ConfigureAwait(false);
                    var payload = state.Invoke();
                    foreach (var definition in DurableStreamStore.Definitions(payload))
                    {
                        if (master.IsMaster && NeedsSweep(payload, definition))
                            await service.MutateAsync(new() { Action = DurableStreamAction.Sweep, Stream = definition.Name }, stoppingToken).ConfigureAwait(false);
                        foreach (var subscription in DurableStreamStore.Subscriptions(payload, definition.Name).Where(item => item.Active))
                        {
                            var function = service.Function(subscription.Function);
                            if (function is null || (function.PodType != PodType.WebSocket && !master.IsMaster))
                                continue;
                            await DispatchAsync(definition, function, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    foreach (string name in registry.GetRegisteredFunctionNames())
                        foreach (var connection in registry.GetConnections(name))
                            foreach (string element in connection.StreamRequests.Keys)
                                if (DurableStreamStore.Attempt(state.Invoke(), element)?.State is not ("Running" or "WaitingForCallback"))
                                    connection.StreamRequests.TryRemove(element, out _);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogStreamDispatcherFailed(exception); }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(_requests.Values).ConfigureAwait(false); }
    }

    private async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        foreach (var function in replicas.Deployments.Functions)
        {
            string signature = string.Join(',', (function.SubscribeStreams ?? []).Select(item => item.Visibility + ":" + item.Name)) +
                FormattableString.Invariant($";{function.StreamMaxAgeSeconds};{function.StreamMaxLength};{function.NumberParallelRequest};{function.NumberParallelRequestPerPod};{function.Visibility}");
            if (_configuration.GetValueOrDefault(function.Deployment) == signature)
                continue;
            string? error = await StreamConfiguration.ApplyAsync(service, function, null, cancellationToken).ConfigureAwait(false);
            if (error is not null)
                logger.LogStreamConfigurationRejected(string.Join(',', (function.SubscribeStreams ?? []).Select(item => item.Name)), function.Deployment, error);
            else
                _configuration[function.Deployment] = signature;
        }
    }

    private static bool NeedsSweep(SlimDataPayload payload, DurableStreamDefinition definition)
    {
        long now = DateTime.UtcNow.Ticks;
        var first = DurableStreamStore.Messages(payload, definition, definition.Earliest - 1, 1).FirstOrDefault();
        return first is not null && first.AppendedAtTicks <= now - definition.MaxAgeSeconds * TimeSpan.TicksPerSecond ||
            DurableStreamStore.Subscriptions(payload, definition.Name).Any(subscription =>
                DurableStreamStore.Deliveries(payload, definition.Name, subscription.Function)
                    .Any(delivery => delivery.State is "Running" or "WaitingForCallback" && delivery.DeadlineTicks <= now));
    }

    private async Task DispatchAsync(DurableStreamDefinition definition, DeploymentInformation function, CancellationToken cancellationToken)
    {
        var payload = state.Invoke();
        var subscription = DurableStreamStore.Subscription(payload, definition.Name, function.Deployment);
        if (subscription is null) return;
        if (function.DependsOn?.Any(dependency => service.Function(dependency)?.Pods.Any(pod => pod.Ready == true) != true) == true)
            return;
        var counters = DurableStreamStore.Counters(payload, definition, subscription);
        if (counters.Lag <= counters.PendingCount &&
            !DurableStreamStore.Deliveries(payload, definition.Name, function.Deployment)
                .Any(delivery => delivery.State == "RetryScheduled" && delivery.RetryAtTicks <= DateTime.UtcNow.Ticks))
            return;
        var retry = function.Configuration.DefaultAsync;
        var candidates = function.Pods.Where(pod => pod.Ready == true && !string.IsNullOrEmpty(pod.Ip))
            .OrderBy(pod => DurableStreamStore.Running(state.Invoke(), function.Deployment, Proxy.RoutingTarget(pod))).ToArray();
        foreach (var pod in candidates)
        {
            var result = await service.MutateAsync(new()
            {
                Action = DurableStreamAction.Reserve, Stream = definition.Name, Function = function.Deployment,
                Owner = _owner, Replica = Proxy.RoutingTarget(pod), MaximumParallel = function.NumberParallelRequest,
                MaximumPerReplica = function.NumberParallelRequestPerPod, TimeoutSeconds = retry.HttpTimeout,
                Retries = retry.TimeoutRetries.ToArray(), RetryStatuses = retry.HttpStatusRetries.ToArray()
            }, cancellationToken).ConfigureAwait(false);
            if (result.Delivery is not { } delivery) continue;
            var value = DurableStreamStore.Body(state.Invoke(), definition.Name, delivery.MessageId);
            if (value.IsEmpty)
            {
                await CompleteAsync(delivery, 410, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var request = MemoryPackSerializer.Deserialize<CustomRequest>(value.Span) with { FunctionName = function.Deployment };
            request.Headers.RemoveAll(header => header.Key.StartsWith("SlimFaas-", StringComparison.OrdinalIgnoreCase));
            request.Headers.Add(new(SlimQueuesWorker.SlimfaasElementId, [delivery.ElementId]));
            request.Headers.Add(new(SlimQueuesWorker.SlimfaasTryNumber, [delivery.Attempt.ToString(CultureInfo.InvariantCulture)]));
            request.Headers.Add(new(SlimQueuesWorker.SlimfaasLastTry, [(delivery.Attempt > retry.TimeoutRetries.Count).ToString()]));
            request.Headers.Add(new("SlimFaas-Stream-Name", [definition.Name]));
            request.Headers.Add(new("SlimFaas-Stream-Message-Id", [delivery.MessageId]));
            if (function.PodType == PodType.WebSocket)
                await SendWebSocketAsync(delivery, request, cancellationToken).ConfigureAwait(false);
            else
                _requests.Add(delivery.ElementId, SendHttpAsync(delivery, request, pod, retry, cancellationToken));
        }
    }

    private async Task SendWebSocketAsync(DurableStreamDelivery delivery, CustomRequest request, CancellationToken cancellationToken)
    {
        var connection = registry.GetConnections(delivery.Function).FirstOrDefault(item => item.ConnectionId == delivery.Replica);
        if (connection is null)
        {
            await CompleteAsync(delivery, 503, cancellationToken).ConfigureAwait(false);
            return;
        }
        connection.StreamRequests[delivery.ElementId] = 0;
        try
        {
            await connection.SendAsync(new()
            {
                Type = WebSocketMessageType.AsyncRequest, CorrelationId = delivery.ElementId,
                Payload = JsonSerializer.SerializeToElement(new AsyncRequestPayload
                {
                    ElementId = delivery.ElementId, Method = "POST", Path = request.Path, Query = request.Query,
                    Headers = request.Headers.ToDictionary(header => header.Key, header => header.Values.Select(value => value ?? "").ToArray()),
                    Body = request.Body is null ? null : Convert.ToBase64String(request.Body),
                    TryNumber = delivery.Attempt, IsLastTry = delivery.Attempt > delivery.Retries.Length,
                    StreamName = delivery.Stream, MessageId = delivery.MessageId
                }, AppJsonContext.Default.AsyncRequestPayload)
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogStreamDispatcherFailed(exception);
            connection.StreamRequests.TryRemove(delivery.ElementId, out _);
            await CompleteAsync(delivery, 503, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendHttpAsync(DurableStreamDelivery delivery, CustomRequest request, PodInformation pod,
        SlimFaasDefaultConfiguration retry, CancellationToken cancellationToken)
    {
        int code;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var response = await sender.SendStreamHttpRequestAsync(request,
                retry with { TimeoutRetries = [], HttpStatusRetries = [] },
                baseUrl: SlimDataEndpoint.Get(pod, options.Value.BaseFunctionPodUrl, namespaceProvider.CurrentNamespace),
                cancellationToken: cancellation, reservedPodIp: pod.Ip).ConfigureAwait(false);
            code = (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { code = 504; }
        catch (HttpRequestException exception) { logger.LogStreamDispatcherFailed(exception); code = 503; }
        await CompleteAsync(delivery, code, cancellationToken).ConfigureAwait(false);
    }

    private Task<DurableStreamMutationResult> CompleteAsync(DurableStreamDelivery delivery, int code, CancellationToken cancellationToken) =>
        service.MutateAsync(new()
        {
            Action = DurableStreamAction.Complete, Stream = delivery.Stream, Function = delivery.Function,
            ElementId = delivery.ElementId, StatusCode = code
        }, cancellationToken);
}
