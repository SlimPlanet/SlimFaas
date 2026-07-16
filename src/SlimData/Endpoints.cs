using DotNext;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using MemoryPack;
using Microsoft.AspNetCore.Connections;
using SlimData.Commands;

namespace SlimData;

[MemoryPackable]
public partial record ListLeftPushInput(byte[] Value, byte[] RetryInformation, string? NewElementId = null);

[MemoryPackable]
public partial record ListLeftPushBatchItem(string Key, byte[] Payload); // Payload = serialize de ListLeftPushInput

[MemoryPackable]
public partial record ListLeftPushBatchRequest(ListLeftPushBatchItem[] Items);

[MemoryPackable]
public partial record ListLeftPushBatchResponse(string[] ElementIds);

[MemoryPackable]
public partial record RetryInformation(List<int> Retries, int RetryTimeoutSeconds, List<int> HttpStatusRetries);

[MemoryPackable]
public partial record QueueItemStatus(string Id = "", int HttpCode = 0);

[MemoryPackable]
public partial record ListQueueItemStatus
{
    public List<QueueItemStatus>? Items { get; set; }
}

[MemoryPackable]
public partial record struct HashsetSet(string Key, IDictionary<string, byte[]> Values);

[MemoryPackable]
public partial record struct ListCallbackBatchItem(string Key, byte[] Payload);

[MemoryPackable]
public partial record struct ListCallbackBatchRequest(ListCallbackBatchItem[] Items);

[MemoryPackable]
public partial record struct ListCallbackBatchResponse(bool[] Acks);

[MemoryPackable]
public partial record struct KeyValueBatchItem(
    KeyValueOperation Operation,
    string Key,
    byte[] Value,
    long? ExpireAtUtcTicks,
    long IntegerDelta,
    decimal FloatDelta,
    long NowTicks);

[MemoryPackable]
public partial record struct KeyValueBatchRequest(KeyValueBatchItem[] Items);

[MemoryPackable]
public partial record struct KeyValueBatchResponse(KeyValueCommandResult[] Results);

public sealed record LpReq(
    IRaftCluster Cluster,
    ListLeftPushBatchRequest Request,
    CancellationToken Ct
);

public class Endpoints
{
    private static readonly TimeSpan ReplicationTimeout = TimeSpan.FromSeconds(5);
    public delegate Task RespondDelegate(IRaftCluster cluster, SlimPersistentState provider,
        CancellationTokenSource? source);

    // Taille: 2–4 × (nombre de followers) est un bon départ
    private static readonly SemaphoreSlim Inflight = new(initialCount: 8);

    private static string ResolveElementId(string? bodyElementId)
    {
        if (!string.IsNullOrWhiteSpace(bodyElementId))
            return bodyElementId;
        return Guid.NewGuid().ToString();
    }

    private static long? ToExpireAtUtcTicksFromQuery(HttpContext ctx)
    {
        if (!ctx.Request.Query.TryGetValue("ttl", out var ttlStr)) return null;
        if (!long.TryParse(ttlStr.ToString(), out var ttlMilliseconds)) return null;

        var now = DateTime.UtcNow.Ticks;
        if (ttlMilliseconds <= 0) return now;

        var add = ttlMilliseconds * TimeSpan.TicksPerMillisecond;
        var expire = now + add;
        if (expire < now) return long.MaxValue; // overflow -> "never expires"
        return expire;
    }

    private static async Task<bool> SafeReplicateAsync<T>(IRaftCluster cluster, T cmd, CancellationToken ct)
        where T : IInputLogEntry
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct, cluster.LeadershipToken);
        timeoutSource.CancelAfter(ReplicationTimeout);
        var operationToken = timeoutSource.Token;

        // Évite le head-of-line : ne bloque pas indéfiniment
        if (!await Inflight.WaitAsync(ReplicationTimeout, operationToken))
            throw new TooManyRequestsException(); // 429
        try
        {
            await WaitForWritableLeaderAsync(cluster, operationToken).ConfigureAwait(false);
            await cluster.ReplicateAsync(cmd, operationToken).ConfigureAwait(false);
            if (cmd.Context is CommandApplyContext applyContext)
            {
                await applyContext.WaitAsync(operationToken).ConfigureAwait(false);
                if (applyContext.IsSkipped)
                {
                    throw new SlimDataUnavailableException(
                        applyContext.ErrorMessage ?? "SlimData skipped an incompatible Raft command.");
                }
            }
            return true;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new SlimDataUnavailableException("Raft quorum or leader lease is unavailable.", ex);
        }
        catch (QuorumUnreachableException ex)
        {
            throw new SlimDataUnavailableException("Raft quorum is unavailable.", ex);
        }
        finally
        {
            Inflight.Release();
        }
    }

    private static async Task WaitForWritableLeaderAsync(IRaftCluster cluster, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (cluster.LeadershipToken.IsCancellationRequested)
                throw new SlimDataUnavailableException("Node is not the active Raft leader.");

            if (!cluster.ConsensusToken.IsCancellationRequested &&
                cluster.TryGetLeaseToken(out var leaseToken) &&
                !leaseToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    public static Task RedirectToLeaderAsync(HttpContext context)
    {
        var cluster = context.RequestServices.GetRequiredService<IRaftCluster>();
        return context.Response.WriteAsync(
            $"Leader address is {cluster.Leader?.EndPoint}. Current address is {context.Connection.LocalIpAddress}:{context.Connection.LocalPort}",
            context.RequestAborted);
    }

    public static async Task AnnounceMemberAsync(HttpContext context)
    {
        var slimDataInfo = context.RequestServices.GetRequiredService<SlimDataInfo>();
        if (context.Connection.LocalPort != slimDataInfo.Port)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.Request.Headers.TryGetValue(SlimDataCommandProtocol.HeaderName, out var protocol) ||
            !string.Equals(protocol.ToString(), SlimDataCommandProtocol.Current, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.Headers[SlimDataCommandProtocol.HeaderName] = SlimDataCommandProtocol.Current;
            context.Response.Headers[SlimDataCommandProtocol.AssemblyVersionHeaderName] =
                SlimDataCommandProtocol.AssemblyVersion;
            return;
        }

        if (!context.Request.Query.TryGetValue("endpoint", out var endpointValue) ||
            !Uri.TryCreate(endpointValue.ToString(), UriKind.Absolute, out var endpoint) ||
            !Startup.IsAllowedClusterMember(endpoint))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var cluster = context.RequestServices.GetRequiredService<IRaftHttpCluster>();
        if (Startup.SameEndpoint(cluster.LocalMemberAddress, endpoint) ||
            ((IRaftCluster)cluster).Members.Any(member =>
                member.EndPoint is UriEndPoint address && Startup.SameEndpoint(address.Uri, endpoint)))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        try
        {
            var coordinator = context.RequestServices.GetRequiredService<ClusterMembershipCoordinator>();
            var added = await coordinator.AddMemberAsync(endpoint, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = added
                ? StatusCodes.Status204NoContent
                : StatusCodes.Status503ServiceUnavailable;
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuorumUnreachableException or NotLeaderException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    public static async Task ProtocolAsync(HttpContext context)
    {
        var slimDataInfo = context.RequestServices.GetRequiredService<SlimDataInfo>();
        if (context.Connection.LocalPort != slimDataInfo.Port)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/plain";
        context.Response.Headers[SlimDataCommandProtocol.HeaderName] = SlimDataCommandProtocol.Current;
        context.Response.Headers[SlimDataCommandProtocol.AssemblyVersionHeaderName] =
            SlimDataCommandProtocol.AssemblyVersion;
        await context.Response.WriteAsync(SlimDataCommandProtocol.Current, context.RequestAborted)
            .ConfigureAwait(false);
    }

    public static async Task DoAsync(HttpContext context, RespondDelegate respondDelegate)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SlimData.Endpoints");
        
        var slimDataInfo = context.RequestServices.GetRequiredService<SlimDataInfo>();
        int[] currentPorts = [context.Connection.LocalPort, context.Request.Host.Port ?? 0];

        if (!currentPorts.Contains(slimDataInfo.Port))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var protocolCompatibility = context.RequestServices.GetRequiredService<ISlimDataProtocolCompatibility>();
        if (!protocolCompatibility.IsCompatible)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var cluster = context.RequestServices.GetRequiredService<IRaftCluster>();
        var provider = context.RequestServices.GetRequiredService<SlimPersistentState>();
        var source = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
            cluster.LeadershipToken);

        try
        {
            await respondDelegate(cluster, provider, source);
        }
        catch (TooManyRequestsException)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        }
        catch (AbandonedMutexException)
        {
            // Leader changed during request
            context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (OperationCanceledException)
        {
            // client disconnected / leadership lost
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest; // (Kestrel non-standard, but useful)
        }
        catch (SlimDataUnavailableException e)
        {
            logger.LogWarning(e, "SlimData is unavailable for {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Unexpected error on {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
        finally
        {
            source?.Dispose();
        }
    }

    public static Task AddHashSetAsync(HttpContext context)
    {
        return DoAsync(context, async (cluster, provider, source) =>
        {
            var expireAtUtcTicks = ToExpireAtUtcTicksFromQuery(context);

            await using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, source!.Token);
            var value = memoryStream.ToArray();

            var hashsetSet = MemoryPackSerializer.Deserialize<HashsetSet>(value);
            await AddHashSetCommand(provider, hashsetSet.Key, hashsetSet.Values, expireAtUtcTicks, cluster, source);
        });
    }

    public static async Task AddHashSetCommand(
        SlimPersistentState provider,
        string key,
        IDictionary<string, byte[]> dictionary,
        long? expireAtUtcTicks,
        IRaftCluster cluster,
        CancellationTokenSource source)
    {
        var value = new Dictionary<string, ReadOnlyMemory<byte>>(dictionary.Count);
        foreach (var kv in dictionary)
            value.Add(kv.Key, kv.Value);

        var command = new AddHashSetCommand
        {
            Key = key,
            Value = value,
            ExpireAtUtcTicks = expireAtUtcTicks
        };
        var context = new CommandApplyContext();
        var logEntry = await SerializedSlimDataLogEntry.CreateAsync(
            command,
            cluster.Term,
            context,
            source.Token).ConfigureAwait(false);

        await SafeReplicateAsync(cluster, logEntry, source.Token);
    }

    public static async Task DeleteHashSetAsync(HttpContext context)
    {
        var task = DoAsync(context, async (cluster, provider, source) =>
        {
            context.Request.Query.TryGetValue("key", out var key);
            if (string.IsNullOrEmpty(key))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("not data found", context.RequestAborted);
                return;
            }

            context.Request.Query.TryGetValue("dictionaryKey", out var dictionaryKey);
            await DeleteHashSetCommand(provider, key.ToString(), dictionaryKey.ToString(), cluster, source!);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        await task;
    }

    public static async Task DeleteHashSetCommand(SlimPersistentState provider, string key, string dictionaryKey,
        IRaftCluster cluster, CancellationTokenSource source)
    {
        var command = new DeleteHashSetCommand { Key = key, DictionaryKey = dictionaryKey };
        var context = new CommandApplyContext();
        var logEntry = await SerializedSlimDataLogEntry.CreateAsync(
            command,
            cluster.Term,
            context,
            source.Token).ConfigureAwait(false);

        await SafeReplicateAsync(cluster, logEntry, source.Token);
    }

    public static Task ListRightPopAsync(HttpContext context)
    {
        return DoAsync(context, async (cluster, provider, source) =>
        {
            var form = await context.Request.ReadFormAsync(source!.Token);

            var (key, value) = GetKeyValue(form);
            context.Request.Query.TryGetValue("transactionId", out var transactionId);
            var reservedIps = context.Request.Query.TryGetValue("reservedIps", out var reservedIpsRaw)
                ? reservedIpsRaw.ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Uri.UnescapeDataString)
                    .ToList()
                : new List<string>();

            if (string.IsNullOrEmpty(key) || !int.TryParse(value, out var count) || string.IsNullOrEmpty(transactionId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("GetKeyValue key or transactionId is empty or value is not a number", context.RequestAborted);
                return;
            }

            var values = await ListRightPopCommand(provider, key, transactionId, count, reservedIps, cluster, source);
            var bin = MemoryPackSerializer.Serialize(values);
            await context.Response.Body.WriteAsync(bin, context.RequestAborted);
        });
    }

    public static async Task<ListItems> ListRightPopCommand(SlimPersistentState provider, string key, string transactionId, int count, List<string>? reservedIps, IRaftCluster cluster,
        CancellationTokenSource source)
    {
        var values = new ListItems();
        values.Items = new List<QueueData>();

        var nowTicks = DateTime.UtcNow.Ticks;
        var command = new SlimData.Commands.ListRightPopCommand
        {
            Key = key,
            Count = count,
            NowTicks = nowTicks,
            IdTransaction = transactionId,
            ReservedIps = reservedIps ?? []
        };
        var context = new CommandApplyContext();
        var logEntry = await SerializedSlimDataLogEntry.CreateAsync(
            command,
            cluster.Term,
            context,
            source.Token).ConfigureAwait(false);
        await SafeReplicateAsync(cluster, logEntry, source.Token);
        await Task.Delay(2, source.Token);

        var supplier = (ISupplier<SlimDataPayload>)provider;
        int numberTry = 10;
        int delayMs = 4;

        while (values.Items.Count == 0 && numberTry-- > 0)
        {
            try
            {
                var queues = supplier.Invoke().Queues;
                if (queues.TryGetValue(key, out var queue))
                {
                    foreach (var qe in queue.GetQueueRunningElement(nowTicks))
                    {
                        var last = qe.RetryQueueElements[^1];
                        if (last.IdTransaction != transactionId) continue;
                        
                        values.Items.Add(new QueueData(qe.Id, qe.Value.ToArray(), qe.NumberOfTries(), qe.IsLastTry(), qe.GetLastRetryTimeTicks(), qe.GetHttpTimeoutTicks(), qe.GetLastReservedIp()));
                    }
                }

                if (values.Items.Count == 0)
                {
                    await Task.Delay(delayMs, source.Token);
                    delayMs = Math.Min(100, delayMs * 2); // backoff 4,8,16,32,64,100...
                }
            }
            catch (Exception ex)
            {
                await Task.Delay(delayMs, source.Token);
                delayMs = Math.Min(100, delayMs * 2);
            }
        }

        return values;
    }

    public static async Task ListLeftPushBatchAsync(HttpContext context)
    {
        var task = DoAsync(context, async (cluster, provider, source) =>
        {
            await using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, source!.Token);
            var value = memoryStream.ToArray();

            var resp = await ListLeftPushBatchCommand(cluster, value, source);
            var responseBytes = MemoryPackSerializer.Serialize(resp);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = responseBytes.Length;

            await context.Response.Body.WriteAsync(responseBytes, 0, responseBytes.Length, source.Token);
            await context.Response.Body.FlushAsync(source.Token);
        });
        await task;
    }

    public static async Task<ListLeftPushBatchResponse> ListLeftPushBatchCommand(IRaftCluster cluster, byte[] value,
        CancellationTokenSource source)
    {
        var listLeftPushBatchRequest = MemoryPackSerializer.Deserialize<ListLeftPushBatchRequest>(value);

        List<ListLeftPushBatchCommand.BatchItem> batchItems = new(listLeftPushBatchRequest.Items.Length);
        foreach (var item in listLeftPushBatchRequest.Items)
        {
            var key = item.Key;
            var listLeftPushInput = MemoryPackSerializer.Deserialize<ListLeftPushInput>(item.Payload);
            var retryInformation = MemoryPackSerializer.Deserialize<RetryInformation>(listLeftPushInput.RetryInformation);

            var batchItem = new ListLeftPushBatchCommand.BatchItem();
            batchItem.Key = key;
            batchItem.Value = new ArraySegment<byte>(listLeftPushInput.Value);
            batchItem.HttpStatusCodesWorthRetrying = retryInformation.HttpStatusRetries;
            batchItem.RetryTimeout = retryInformation.RetryTimeoutSeconds;
            batchItem.Retries = retryInformation.Retries;
            batchItem.Identifier = ResolveElementId(listLeftPushInput.NewElementId);
            batchItem.NowTicks = DateTime.UtcNow.Ticks;

            batchItems.Add(batchItem);
        }

        var command = new SlimData.Commands.ListLeftPushBatchCommand
        {
            Items = batchItems
        };
        var context = new CommandApplyContext();
        var logEntry = await SerializedSlimDataLogEntry.CreateAsync(
            command,
            cluster.Term,
            context,
            source.Token).ConfigureAwait(false);

        bool success = await SafeReplicateAsync(cluster, logEntry, source.Token);
        var listLeftPushBatchResponse = new ListLeftPushBatchResponse(batchItems.Select(b => success ? b.Identifier : "").ToArray());

        return listLeftPushBatchResponse;
    }

    public static Task ListCallbackAsync(HttpContext context)
    {
        return DoAsync(context, async (cluster, provider, source) =>
        {
            context.Request.Query.TryGetValue("key", out var key);
            if (string.IsNullOrEmpty(key))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("not data found", context.RequestAborted);
                return;
            }

            await using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, source!.Token);
            var value = memoryStream.ToArray();
            
            var list = MemoryPackSerializer.Deserialize<ListQueueItemStatus>(value);
            await ListCallbackCommandAsync(provider, key, list, cluster, source);
        });
    }

    public static async Task ListCallbackCommandAsync(SlimPersistentState provider, string key, ListQueueItemStatus list, IRaftCluster cluster, CancellationTokenSource source)
    {
        if (list.Items == null)
        {
            return;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        List<CallbackElement> callbackElements = new(list.Items.Count);
        foreach (var queueItemStatus in list.Items)
        {
            callbackElements.Add(new CallbackElement(queueItemStatus.Id, queueItemStatus.HttpCode));
        }

        var command = new SlimData.Commands.ListCallbackCommand
        {
            Key = key,
            NowTicks = nowTicks,
            CallbackElements = callbackElements
        };
        var context = new CommandApplyContext();
        var logEntry = await SerializedSlimDataLogEntry.CreateAsync(
            command,
            cluster.Term,
            context,
            source.Token).ConfigureAwait(false);

        await SafeReplicateAsync(cluster, logEntry, source.Token);
    }

    public static async Task<ListCallbackBatchResponse> ListCallbackBatchCommand(
        IRaftCluster cluster,
        byte[] value,
        CancellationTokenSource source)
    {
        var req = MemoryPackSerializer.Deserialize<ListCallbackBatchRequest>(value);

        var acks = new bool[req.Items.Length];
        if (req.Items.Length == 0)
            return new ListCallbackBatchResponse(acks);

        var nowTicks = DateTime.UtcNow.Ticks;
        var items = new List<SlimData.Commands.ListCallbackBatchCommand.BatchItem>(req.Items.Length);

        for (int i = 0; i < req.Items.Length; i++)
        {
            var item = req.Items[i];

            var status = MemoryPackSerializer.Deserialize<ListQueueItemStatus>(item.Payload);
            if (status?.Items is null || status.Items.Count == 0)
            {
                acks[i] = true;
                continue;
            }

            var elements = new List<CallbackElement>(status.Items.Count);
            foreach (var s in status.Items)
                elements.Add(new CallbackElement(s.Id, s.HttpCode));

            items.Add(new SlimData.Commands.ListCallbackBatchCommand.BatchItem
            {
                Key = item.Key,
                NowTicks = nowTicks,
                CallbackElements = elements
            });

            acks[i] = true;
        }

        if (items.Count == 0)
            return new ListCallbackBatchResponse(acks);

        var command = new SlimData.Commands.ListCallbackBatchCommand
        {
            Items = items
        };
        var context = new CommandApplyContext();
        var logEntry = await SerializedSlimDataLogEntry.CreateAsync(
            command,
            cluster.Term,
            context,
            source.Token).ConfigureAwait(false);

        await SafeReplicateAsync(cluster, logEntry, source.Token);
        return new ListCallbackBatchResponse(acks);
    }

    public static async Task ListCallbackBatchAsync(HttpContext context)
    {
        var task = DoAsync(context, async (cluster, provider, source) =>
        {
            await using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, source!.Token);
            var value = memoryStream.ToArray();

            var resp = await ListCallbackBatchCommand(cluster, value, source);
            var bytes = MemoryPackSerializer.Serialize(resp);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, 0, bytes.Length, source.Token);
            await context.Response.Body.FlushAsync(source.Token);
        });
        await task;
    }

    private static (string key, string value) GetKeyValue(IFormCollection form)
    {
        var key = string.Empty;
        var value = string.Empty;

        if (form.Count > 0)
        {
            var keyValue = form.First();
            key = keyValue.Key;
            value = keyValue.Value.ToString();
        }

        return (key, value);
    }

    public static Task AddKeyValueBatchAsync(HttpContext context)
    {
        return DoAsync(context, async (cluster, provider, source) =>
        {
            await using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, source!.Token);
            var request = MemoryPackSerializer.Deserialize<KeyValueBatchRequest>(memoryStream.ToArray());

            var response = await AddKeyValueBatchCommand(request, cluster, source);
            var responseBytes = MemoryPackSerializer.Serialize(response);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = responseBytes.Length;
            await context.Response.Body.WriteAsync(responseBytes, source.Token);
        });
    }

    public static async Task<KeyValueBatchResponse> AddKeyValueBatchCommand(
        KeyValueBatchRequest request,
        IRaftCluster cluster,
        CancellationTokenSource source)
    {
        var requestItems = request.Items ?? Array.Empty<KeyValueBatchItem>();
        if (requestItems.Length == 0)
            return new KeyValueBatchResponse(Array.Empty<KeyValueCommandResult>());

        var results = Enumerable.Range(0, requestItems.Length)
            .Select(_ => new KeyValueCommandResult())
            .ToArray();

        var context = new KeyValueCommandBatchContext(results);
        var command = new AddKeyValueCommand
        {
            Items = requestItems
                .Select(item => new AddKeyValueCommand.BatchItem
                {
                    Operation = item.Operation,
                    Key = item.Key,
                    Value = item.Value ?? Array.Empty<byte>(),
                    ExpireAtUtcTicks = item.Operation == KeyValueOperation.Set ? item.ExpireAtUtcTicks : null,
                    IntegerDelta = item.IntegerDelta,
                    FloatDelta = item.FloatDelta,
                    NowTicks = item.NowTicks
                })
                .ToList()
        };
        var payload = command.Serialize();
        SlimDataCommandCodec.ValidateCurrentEnvelope(payload, nameof(AddKeyValueCommand));
        var logEntry = new SerializedSlimDataLogEntry(
            AddKeyValueCommand.Id,
            cluster.Term,
            payload,
            context);

        var success = await SafeReplicateAsync(cluster, logEntry, source.Token);
        if (!success)
        {
            SetNotCommitted(results, "Command was not committed by the Raft quorum.");
            return new KeyValueBatchResponse(results);
        }

        var delayMs = 1;
        for (var i = 0; i < 10 && results.Any(result => result.Status == KeyValueCommandStatus.None); i++)
        {
            await Task.Delay(delayMs, source.Token);
            delayMs = Math.Min(32, delayMs * 2);
        }

        SetNotCommitted(results, "Command result was not produced by the local state machine.");
        return new KeyValueBatchResponse(results);
    }

    private static void SetNotCommitted(KeyValueCommandResult[] results, string message)
    {
        foreach (var result in results)
        {
            if (result.Status == KeyValueCommandStatus.None)
                result.SetError(KeyValueCommandStatus.NotCommitted, message);
        }
    }

    public static async Task DeleteKeyValueAsync(HttpContext context)
    {
        await DoAsync(context, async (cluster, provider, source) =>
        {
            context.Request.Query.TryGetValue("key", out var key);
            if (string.IsNullOrEmpty(key))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("not data found", context.RequestAborted);
                return;
            }

            await DeleteKeyValueCommand(provider, key!.ToString(), cluster, source!);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
    }

    public static async Task DeleteKeyValueCommand(
        SlimPersistentState provider,
        string key,
        IRaftCluster cluster,
        CancellationTokenSource source)
    {
        var command = new DeleteKeyValueCommand { Key = key };
        var context = new CommandApplyContext();
        var logEntry = await SerializedSlimDataLogEntry.CreateAsync(
            command,
            cluster.Term,
            context,
            source.Token).ConfigureAwait(false);

        await SafeReplicateAsync(cluster, logEntry, source.Token);
    }

}

public class TooManyRequestsException : Exception
{
}

public sealed class SlimDataUnavailableException : Exception
{
    public SlimDataUnavailableException(string message)
        : base(message)
    {
    }

    public SlimDataUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
