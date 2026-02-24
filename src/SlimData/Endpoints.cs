using DotNext;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using MemoryPack;
using SlimData.Commands;

namespace SlimData;

[MemoryPackable]
public partial record ListLeftPushInput(byte[] Value, byte[] RetryInformation);

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

public sealed record LpReq(
    IRaftCluster Cluster,
    ListLeftPushBatchRequest Request,
    CancellationToken Ct
);

public class Endpoints
{
    public delegate Task RespondDelegate(IRaftCluster cluster, SlimPersistentState provider,
        CancellationTokenSource? source);

    // Taille: 2–4 × (nombre de followers) est un bon départ
    private static readonly SemaphoreSlim Inflight = new(initialCount: 8);

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

    private static async Task<bool> SafeReplicateAsync<T>(IRaftCluster cluster, LogEntry<T> cmd, CancellationToken ct)
        where T : struct, ICommand<T>
    {
        // Évite le head-of-line : ne bloque pas indéfiniment
        if (!await Inflight.WaitAsync(TimeSpan.FromMilliseconds(5000), ct))
            throw new TooManyRequestsException(); // 429
        try
        {
            var isLeader = !cluster.LeadershipToken.IsCancellationRequested;
            if (!isLeader)
                throw new AbandonedMutexException("Node is not leader anymore");
            return await cluster.ReplicateAsync(cmd, ct);
        }
        finally
        {
            Inflight.Release();
        }
    }

    public static Task RedirectToLeaderAsync(HttpContext context)
    {
        var cluster = context.RequestServices.GetRequiredService<IRaftCluster>();
        return context.Response.WriteAsync(
            $"Leader address is {cluster.Leader?.EndPoint}. Current address is {context.Connection.LocalIpAddress}:{context.Connection.LocalPort}",
            context.RequestAborted);
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

        var logEntry = new LogEntry<AddHashSetCommand>
        {
            Term = cluster.Term,
            Command = new()
            {
                Key = key,
                Value = value,
                ExpireAtUtcTicks = expireAtUtcTicks
            },
        };

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
        var logEntry = new LogEntry<DeleteHashSetCommand>()
        {
            Term = cluster.Term,
            Command = new() { Key = key, DictionaryKey = dictionaryKey }
        };

        await SafeReplicateAsync(cluster, logEntry, source.Token);
    }

    public static Task ListRightPopAsync(HttpContext context)
    {
        return DoAsync(context, async (cluster, provider, source) =>
        {
            var form = await context.Request.ReadFormAsync(source!.Token);

            var (key, value) = GetKeyValue(form);
            context.Request.Query.TryGetValue("transactionId", out var transactionId);

            if (string.IsNullOrEmpty(key) || !int.TryParse(value, out var count) || string.IsNullOrEmpty(transactionId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("GetKeyValue key or transactionId is empty or value is not a number", context.RequestAborted);
                return;
            }

            var values = await ListRightPopCommand(provider, key, transactionId, count, cluster, source);
            var bin = MemoryPackSerializer.Serialize(values);
            await context.Response.Body.WriteAsync(bin, context.RequestAborted);
        });
    }

    public static async Task<ListItems> ListRightPopCommand(SlimPersistentState provider, string key, string transactionId, int count, IRaftCluster cluster,
        CancellationTokenSource source)
    {
        var values = new ListItems();
        values.Items = new List<QueueData>();

        var nowTicks = DateTime.UtcNow.Ticks;
        var logEntry = new LogEntry<ListRightPopCommand>()
        {
            Term = cluster.Term,
            Command = new() { Key = key, Count = count, NowTicks = nowTicks, IdTransaction = transactionId },
        };
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
                        
                        values.Items.Add(new QueueData(qe.Id, qe.Value.ToArray(), qe.NumberOfTries(), qe.IsLastTry()));
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
            batchItem.Identifier = Guid.NewGuid().ToString();
            batchItem.NowTicks = DateTime.UtcNow.Ticks;

            batchItems.Add(batchItem);
        }

        var logEntry = new LogEntry<ListLeftPushBatchCommand>()
        {
            Term = cluster.Term,
            Command = new()
            {
                Items = batchItems,
            },
        };

        bool success = await SafeReplicateAsync(cluster, logEntry, source.Token);
        var listLeftPushBatchResponse = new ListLeftPushBatchResponse(batchItems.Select(b => success ? b.Identifier : "").ToArray());

        return listLeftPushBatchResponse;
    }

    public static async Task ListLeftPushAsync(HttpContext context)
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

            await using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, source!.Token);
            var value = memoryStream.ToArray();

            string elementId = await ListLeftPushCommand(provider, key, value, cluster, source);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsync(elementId, context.RequestAborted);
        });
        await task;
    }

    public static async Task<string> ListLeftPushCommand(SlimPersistentState provider, string key, byte[] value,
        IRaftCluster cluster, CancellationTokenSource source)
    {
        var input = MemoryPackSerializer.Deserialize<ListLeftPushInput>(value);
        var retryInformation = MemoryPackSerializer.Deserialize<RetryInformation>(input.RetryInformation);
        var id = Guid.NewGuid().ToString();

        var logEntry = new LogEntry<ListLeftPushCommand>()
        {
            Term = cluster.Term,
            Command = new()
            {
                Key = key,
                Identifier = id,
                Value = input.Value,
                NowTicks = DateTime.UtcNow.Ticks,
                Retries = retryInformation.Retries,
                RetryTimeout = retryInformation.RetryTimeoutSeconds,
                HttpStatusCodesWorthRetrying = retryInformation.HttpStatusRetries
            },
        };

        var success = await SafeReplicateAsync(cluster, logEntry, source.Token);
        return success ? id : "";
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

        var logEntry = new LogEntry<ListCallbackCommand>()
        {
            Term = cluster.Term,
            Command = new()
            {
                Key = key,
                NowTicks = nowTicks,
                CallbackElements = callbackElements
            },
        };

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

        var logEntry = new LogEntry<SlimData.Commands.ListCallbackBatchCommand>
        {
            Term = cluster.Term,
            Command = new SlimData.Commands.ListCallbackBatchCommand
            {
                Items = items
            },
        };

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

    public static Task AddKeyValueAsync(HttpContext context)
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

            var expireAtUtcTicks = ToExpireAtUtcTicksFromQuery(context);

            await using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, source!.Token);
            var value = memoryStream.ToArray();

            await AddKeyValueCommand(provider, key!, value, expireAtUtcTicks, cluster, source);
        });
    }

    public static async Task AddKeyValueCommand(
        SlimPersistentState provider,
        string key,
        byte[] value,
        long? expireAtUtcTicks,
        IRaftCluster cluster,
        CancellationTokenSource source)
    {
        var logEntry = new LogEntry<AddKeyValueCommand>()
        {
            Term = cluster.Term,
            Command = new() { Key = key, Value = value, ExpireAtUtcTicks = expireAtUtcTicks },
        };

        await SafeReplicateAsync(cluster, logEntry, source.Token);
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
        var logEntry = new LogEntry<DeleteKeyValueCommand>
        {
            Term = cluster.Term,
            Command = new() { Key = key }
        };

        await SafeReplicateAsync(cluster, logEntry, source.Token);
    }

    public const string ScheduleJobPrefix = "ScheduleJob:";
}

public class TooManyRequestsException : Exception
{
}
