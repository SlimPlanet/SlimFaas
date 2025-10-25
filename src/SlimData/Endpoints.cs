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
public partial record QueueItemStatus(string Id="", int HttpCode=0);

[MemoryPackable]
public partial record ListQueueItemStatus
{
    public List<QueueItemStatus>? Items { get; set; }
}


[MemoryPackable]
public partial record struct HashsetSet(string Key, IDictionary<string, byte[]> Values);



public class Endpoints
{
    public delegate Task RespondDelegate(IRaftCluster cluster, SlimPersistentState provider,
        CancellationTokenSource? source);
    
    // Taille: 2–4 × (nombre de followers) est un bon départ
    private static readonly SemaphoreSlim Inflight = new(initialCount: 16);

    private static async Task<bool> SafeReplicateAsync<T>(IRaftCluster cluster, LogEntry<T> cmd, CancellationToken ct)
        where T : struct, ICommand<T>
    {
        // Évite le head-of-line : ne bloque pas indéfiniment
        if (!await Inflight.WaitAsync(TimeSpan.FromMilliseconds(2000), ct))
            throw new TooManyRequestsException(); // ou return 429
        try
        {
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
        catch (Exception e)
        {
            Console.WriteLine("Unexpected error {0}", e);
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
            var inputStream = context.Request.Body;
            await using var memoryStream = new MemoryStream();
            await inputStream.CopyToAsync(memoryStream, source.Token);
            var value = memoryStream.ToArray();
            var hashsetSet =  MemoryPackSerializer.Deserialize<HashsetSet>(value);
            await AddHashSetCommand(provider, hashsetSet.Key, hashsetSet.Values, cluster, source);
        });
    }

    public static async Task AddHashSetCommand(SlimPersistentState provider, string key, IDictionary<string, byte[]> dictionary,
        IRaftCluster cluster, CancellationTokenSource source)
    {
        var value = new Dictionary<string, ReadOnlyMemory<byte>>(dictionary.Count);
        foreach (var keyValue in dictionary)
        {
            value.Add(keyValue.Key, keyValue.Value);
        }

        var logEntry = new LogEntry<AddHashSetCommand>()
        {
            Term = cluster.Term,
            Command = new() { Key = key, Value = value },
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
            await DeleteHashSetCommand(provider, key.ToString(), dictionaryKey.ToString(), cluster, source);
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
            var form = await context.Request.ReadFormAsync(source.Token);

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
        while (values.Items.Count <= 0 && numberTry > 0)
        {
            numberTry--;
            try
            {
                var queues = supplier.Invoke().Queues;
                if (queues.TryGetValue(key, out var queue))
                {
                    var queueElements = queue.GetQueueRunningElement(nowTicks)
                        .Where(q => q.RetryQueueElements[^1].IdTransaction == transactionId).ToList();
                    if (queueElements.Count == 0)
                    {
                        await Task.Delay(4, source.Token);
                    }

                    foreach (var queueElement in queueElements)
                    {
                        values.Items.Add(new QueueData(queueElement.Id, queueElement.Value.ToArray()));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unexpected error {0}", ex);
            }
        }
            
        return values;
    }
    
    public static async Task ListLeftPushBatchAsync(HttpContext context)
    {
        var task = DoAsync(context, async (cluster, provider, source) =>
        {
            var inputStream = context.Request.Body;
            await using var memoryStream = new MemoryStream();
            await inputStream.CopyToAsync(memoryStream, source.Token);
            var value = memoryStream.ToArray();
            var listLeftPushBatchCommand = await ListLeftPushBatchCommand(cluster, value, source);
            context.Response.StatusCode = StatusCodes.Status201Created;
            var responseBytes = MemoryPackSerializer.Serialize(listLeftPushBatchCommand);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = responseBytes.Length;

            await context.Response.Body.WriteAsync(responseBytes, 0, responseBytes.Length, source.Token);
            await context.Response.Body.FlushAsync(source.Token);

        });
        await task;
    }
    
    public static async Task<ListLeftPushBatchResponse> ListLeftPushBatchCommand(IRaftCluster cluster, byte[] value
        , CancellationTokenSource source)
    {
        
        //var bin = MemoryPackSerializer.Serialize(value);
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
            batchItem.HttpStatusCodesWorthRetrying = retryInformation.HttpStatusRetries;
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

        await SafeReplicateAsync(cluster, logEntry, source.Token);
        var listLeftPushBatchResponse = new ListLeftPushBatchResponse(batchItems.Select(b => b.Identifier).ToArray());

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
            
            var inputStream = context.Request.Body;
            await using var memoryStream = new MemoryStream();
            await inputStream.CopyToAsync(memoryStream, source.Token);
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

        await SafeReplicateAsync(cluster, logEntry, source.Token);
        return id;
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
            
            var inputStream = context.Request.Body;
            await using var memoryStream = new MemoryStream();
            await inputStream.CopyToAsync(memoryStream, source.Token);
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
        List<CallbackElement> callbackElements = new List<CallbackElement>(list.Items.Count);
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
            var inputStream = context.Request.Body;
            await using var memoryStream = new MemoryStream();
            await inputStream.CopyToAsync(memoryStream, source.Token);
            var value = memoryStream.ToArray();
            await AddKeyValueCommand(provider, key, value, cluster, source);
        });
    }

    public static async Task AddKeyValueCommand(SlimPersistentState provider, string key, byte[] value,
        IRaftCluster cluster, CancellationTokenSource source)
    {
        var logEntry = new LogEntry<AddKeyValueCommand>()
        {
            Term = cluster.Term,
            Command = new() { Key = key, Value = value },
        };
        
        await SafeReplicateAsync(cluster, logEntry, source.Token);
    }
}

public class TooManyRequestsException : Exception
{
}