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

    private static async Task<bool> SafeReplicateAsync<T>(IRaftCluster cluster, LogEntry<T> cmd, CancellationToken ct)
        where T : struct, ICommand<T>
    {
        // Évite le head-of-line : ne bloque pas indéfiniment
        if (!await Inflight.WaitAsync(TimeSpan.FromMilliseconds(5000), ct))
            throw new TooManyRequestsException(); // ou return 429
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
  
    private static ListLeftPushBatchRequest DeserializeListLeftPushBatchRequest(byte[] value)
    {
        return MemoryPackSerializer.Deserialize<ListLeftPushBatchRequest>(value);
    }
    /*
  // Batcher pour ListLeftPushBatch : combine les requêtes entrantes côté leader
private static readonly RateAdaptiveBatcher<LpReq, ListLeftPushBatchResponse> _lpBatcher =
  new RateAdaptiveBatcher<LpReq, ListLeftPushBatchResponse>(
      directHandler: async (req, ct) =>
      {
          // Chemin "direct" (débit faible) : une seule requête -> une seule réplication
          var ids = await ReplicateListLeftPushBatchAsync(
              req.Cluster,
              req.Request.Items,
              req.Ct.IsCancellationRequested ? req.Ct : ct);

          // Les IDs renvoyés doivent correspondre au nombre d’items de LA requête
          if (ids.Length != req.Request.Items.Length)
              throw new InvalidOperationException("Inconsistent IDs count in direct handler.");
          return new ListLeftPushBatchResponse(ids);
      },
      batchHandler: async (reqs, ct) =>
      {
          // Regroupe par instance de cluster (sécurité si jamais plusieurs IRaftCluster coexistent)
          var groups = reqs
              .Select((r, idx) => (r, idx))
              .GroupBy(x => x.r.Cluster);

          var results = new ListLeftPushBatchResponse[reqs.Count];

          foreach (var g in groups)
          {
              var cluster = g.Key;
              var groupList = g.ToList(); // (r, idx)

              // Aplatis tous les items du groupe en conservant les tailles pour re-slicer après
              var allItems = new List<ListLeftPushBatchItem>();
              var countsPerReq = new int[groupList.Count];

              for (int i = 0; i < groupList.Count; i++)
              {
                  var items = groupList[i].r.Request.Items;
                  countsPerReq[i] = items.Length;
                  allItems.AddRange(items);
              }

              // Une seule réplication Raft pour tout le groupe
              var anyCt = groupList[0].r.Ct;
              var ctToUse = anyCt.IsCancellationRequested ? ct : anyCt;

              var allIds = await ReplicateListLeftPushBatchAsync(cluster, allItems, ctToUse);

              // Redistribue les IDs à chaque requête d’origine (dans l’ordre)
              int cursor = 0;
              for (int i = 0; i < groupList.Count; i++)
              {
                  var take = countsPerReq[i];
                  var slice = allIds.AsSpan(cursor, take).ToArray();
                  cursor += take;

                  results[groupList[i].idx] = new ListLeftPushBatchResponse(slice);
              }
          }

          return results.ToList().AsReadOnly();
      },
      // Réutilise ta logique de paliers par défaut
      tiers: null,
      // Nombre max de REQUÊTES agrégées par batcher (pas le nb d’items)
      maxBatchSize: 64,
      // File "illimitée" (à ajuster si besoin)
      maxQueueLength: 0,
      // By-pass: si pas de délai imposé par les paliers et un seul item => directHandler
      directBypassDelay: TimeSpan.Zero
  );


  private static async Task<string[]> ReplicateListLeftPushBatchAsync(
      IRaftCluster cluster,
      IEnumerable<ListLeftPushBatchItem> allItems,
      CancellationToken ct)
  {
      var batchItems = new List<SlimData.Commands.ListLeftPushBatchCommand.BatchItem>();

      foreach (var item in allItems)
      {
          var key = item.Key;
          var input = MemoryPackSerializer.Deserialize<ListLeftPushInput>(item.Payload);
          var retry = MemoryPackSerializer.Deserialize<RetryInformation>(input.RetryInformation);

          batchItems.Add(new SlimData.Commands.ListLeftPushBatchCommand.BatchItem
          {
              Key = key,
              Identifier = Guid.NewGuid().ToString(),
              NowTicks = DateTime.UtcNow.Ticks,
              Value = new ArraySegment<byte>(input.Value),
              RetryTimeout = retry.RetryTimeoutSeconds,
              HttpStatusCodesWorthRetrying = retry.HttpStatusRetries
          });
      }

      if (batchItems.Count == 0)
          return Array.Empty<string>();

      var logEntry = new LogEntry<SlimData.Commands.ListLeftPushBatchCommand>
      {
          Term = cluster.Term,
          Command = new SlimData.Commands.ListLeftPushBatchCommand
          {
              Items = batchItems
          },
      };

      await SafeReplicateAsync(cluster, logEntry, ct);

      // Renvoie les IDs dans l’ordre de construction
      return batchItems.Select(b => b.Identifier).ToArray();
  }

*/
    
    public static async Task ListLeftPushBatchAsync(HttpContext context)
    {
        var task = DoAsync(context, async (cluster, provider, source) =>
        {
            var inputStream = context.Request.Body;
            await using var memoryStream = new MemoryStream();
            await inputStream.CopyToAsync(memoryStream, source.Token);
            var value = memoryStream.ToArray();
            // Désérialise en ListLeftPushBatchRequest
            var req = DeserializeListLeftPushBatchRequest(value);
            Console.WriteLine($"Count ListLeftPushBatchAsync: {req.Items.Length}");

            // Enfile dans le batcher (sera combiné avec d’autres appels entrants)
           /* var resp = await _lpBatcher.EnqueueAsync(
                new LpReq(cluster, req, source.Token),
                source.Token
            );*/
           var resp = await ListLeftPushBatchCommand(cluster, value, source);
            context.Response.StatusCode = StatusCodes.Status201Created;
            var responseBytes = MemoryPackSerializer.Serialize(resp);
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
        
        double sizeInKo = value.Length / 1024.0;
        Console.WriteLine($"Taille ListLeftPushBatchCommand : {sizeInKo:F2} Ko");

        var listLeftPushBatchRequest = MemoryPackSerializer.Deserialize<ListLeftPushBatchRequest>(value);
        Console.WriteLine($" Count ListLeftPushBatchCommand: {listLeftPushBatchRequest.Items.Length}");
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
            double sizeInKo = value.Length / 1024.0;
            Console.WriteLine($"Taille ListCallbackAsync : {sizeInKo:F2} Ko");
            var list = MemoryPackSerializer.Deserialize<ListQueueItemStatus>(value);
            Console.WriteLine($" Count ListCallbackAsync: {list.Items.Count}");
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
    
    public static async Task<ListCallbackBatchResponse> ListCallbackBatchCommand(
    IRaftCluster cluster,
    byte[] value,
    CancellationTokenSource source)
{
    // 1) Désérialise la requête batch
    var req = MemoryPackSerializer.Deserialize<ListCallbackBatchRequest>(value);

    // Prépare les ACKs (même longueur que la requête)
    var acks = new bool[req.Items.Length];
    if (req.Items.Length == 0)
        return new ListCallbackBatchResponse(acks);

    // 2) Construit la commande Raft batch (une seule log entry pour tout le lot)
    var nowTicks = DateTime.UtcNow.Ticks;
    var items = new List<SlimData.Commands.ListCallbackBatchCommand.BatchItem>(req.Items.Length);

    for (int i = 0; i < req.Items.Length; i++)
    {
        var item = req.Items[i];

        // Payload = ListQueueItemStatus sérialisé
        var status = MemoryPackSerializer.Deserialize<ListQueueItemStatus>(item.Payload);
        if (status?.Items is null || status.Items.Count == 0)
        {
            // Rien à appliquer : on ack "true" (pas d'erreur), mais on n'ajoute pas d'item vide
            acks[i] = true;
            continue;
        }

        // Convertit en CallbackElements
        var elements = new List<CallbackElement>(status.Items.Count);
        foreach (var s in status.Items)
            elements.Add(new CallbackElement(s.Id, s.HttpCode));

        items.Add(new SlimData.Commands.ListCallbackBatchCommand.BatchItem
        {
            Key = item.Key,
            NowTicks = nowTicks,               // même horodatage pour la cohérence du batch
            CallbackElements = elements
        });

        acks[i] = true;
    }

    // S’il n’y a finalement aucun item utile, on renvoie juste les ACKs
    if (items.Count == 0)
        return new ListCallbackBatchResponse(acks);

    // 3) Réplication Raft : UNE seule entrée pour tout le batch
    var logEntry = new LogEntry<SlimData.Commands.ListCallbackBatchCommand>
    {
        Term = cluster.Term,
        Command = new SlimData.Commands.ListCallbackBatchCommand
        {
            Items = items
        },
    };

    await SafeReplicateAsync(cluster, logEntry, source.Token);

    // 4) Réponse
    return new ListCallbackBatchResponse(acks);
}


    public static async Task ListCallbackBatchAsync(HttpContext context)
    {
        var task = DoAsync(context, async (cluster, provider, source) =>
        {
            var inputStream = context.Request.Body;
            await using var memoryStream = new MemoryStream();
            await inputStream.CopyToAsync(memoryStream, source.Token);
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