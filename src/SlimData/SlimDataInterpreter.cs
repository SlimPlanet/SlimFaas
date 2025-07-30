using System.Collections.Concurrent;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using SlimData.Commands;

namespace SlimData;



public record SlimDataState(
    Dictionary<string, Dictionary<string, string>> Hashsets,
    Dictionary<string, ReadOnlyMemory<byte>> KeyValues,
    Dictionary<string, List<QueueElement>> Queues);

public class QueueElement(
    ReadOnlyMemory<byte> value,
    string id,
    long insertTimeStamp,
    int httpTimeout,
    List<int> timeoutRetries,
    IList<QueueHttpTryElement> retryQueueElements,
    List<int> httpStatusRetries
    )
{
    public ReadOnlyMemory<byte> Value { get; } = value;
    public string Id { get; } = id;
    public long InsertTimeStamp { get; } = insertTimeStamp;
    
    public List<int> TimeoutRetries { get; } = timeoutRetries;

    public int HttpTimeout { get; } = httpTimeout;
    public IList<QueueHttpTryElement> RetryQueueElements { get; } = retryQueueElements;
    
    public List<int> HttpStatusRetries { get; } = httpStatusRetries;
}

public class QueueHttpTryElement(long startTimeStamp=0, string idTransaction="", long endTimeStamp=0, int httpCode=0)
{
    public long StartTimeStamp { get; set; } = startTimeStamp;
    public long EndTimeStamp { get;set; } = endTimeStamp;
    public int HttpCode { get; set; } = httpCode;
    public string IdTransaction { get;set; } = idTransaction;
}

#pragma warning restore CA2252
public class SlimDataInterpreter : CommandInterpreter
{
    public const int DeleteFromQueueCode = 1000;
    public SlimDataState SlimDataState = new(new Dictionary<string, Dictionary<string, string>>(), new Dictionary<string, ReadOnlyMemory<byte>>(), new Dictionary<string, List<QueueElement>>());

    [CommandHandler]
    public ValueTask ListRightPopAsync(ListRightPopCommand addHashSetCommand, CancellationToken token)
    {
        return DoListRightPopAsync(addHashSetCommand, SlimDataState.Queues);
    }
    
    internal static ValueTask DoListRightPopAsync(ListRightPopCommand listRightPopCommand, Dictionary<string, List<QueueElement>> queues)
    {
        if (queues.TryGetValue(listRightPopCommand.Key, out var queue))
        {
            var nowTicks =listRightPopCommand.NowTicks;
            /*var queueTimeoutElements = queue.GetQueueTimeoutElement(nowTicks);
            foreach (var queueTimeoutElement in queueTimeoutElements)
            {
                var retryQueueElement = queueTimeoutElement.RetryQueueElements[^1];
                retryQueueElement.EndTimeStamp = nowTicks;
                retryQueueElement.HttpCode = 504;
            }
            
            var queueFinishedElements = queue.GetQueueFinishedElement(nowTicks);
            foreach (var queueFinishedElement in queueFinishedElements)
            {
                queue.Remove(queueFinishedElement);
            }*/ 
            
            var queueAvailableElements = queue.GetQueueAvailableElement(nowTicks, listRightPopCommand.Count);
            foreach (var queueAvailableElement in queueAvailableElements)
            {
                Console.WriteLine("bbbbbbbbb :Retrieve Id : " + queueAvailableElement.Id);
                queueAvailableElement.RetryQueueElements.Add(new QueueHttpTryElement(nowTicks, listRightPopCommand.IdTransaction));
                //queue.Remove(queueAvailableElement);
            }
            
           /* var listCallbackCommand = listRightPopCommand;
            var value = queue;
            if (value.GetQueueRunningElement(listCallbackCommand.NowTicks).Count > 30)
            {
                Console.WriteLine($"==========> Queue Count {listCallbackCommand.Key}: {value.Count}");
                Console.WriteLine(
                    $"==========> Queue Count IsFinished {listCallbackCommand.Key}: {value.GetQueueFinishedElement(listCallbackCommand.NowTicks).Count}");
                Console.WriteLine(
                    $"==========> Queue Count IsWaitingForRetry {listCallbackCommand.Key}: {value.GetQueueWaitingForRetryElement(listCallbackCommand.NowTicks).Count}");
                Console.WriteLine(
                    $"==========> Queue Count IsRunning {listCallbackCommand.Key}: {value.GetQueueRunningElement(listCallbackCommand.NowTicks).Count}");
                Console.WriteLine(
                    $"==========> Queue Count IsAvailable {listCallbackCommand.Key}: {value.GetQueueAvailableElement(listCallbackCommand.NowTicks, 9999).Count}");
            }*/
        }
        
        return default;
    }

    [CommandHandler]
    public ValueTask ListLeftPushAsync(ListLeftPushCommand addHashSetCommand, CancellationToken token)
    {
        return DoListLeftPushAsync(addHashSetCommand, SlimDataState.Queues);
    }
    
    internal static ValueTask DoListLeftPushAsync(ListLeftPushCommand listLeftPushCommand, Dictionary<string, List<QueueElement>> queues)
    {
        if (queues.TryGetValue(listLeftPushCommand.Key, out List<QueueElement>? value))
            value.Add(new QueueElement(listLeftPushCommand.Value, listLeftPushCommand.Identifier, listLeftPushCommand.NowTicks, listLeftPushCommand.RetryTimeout, listLeftPushCommand.Retries,new List<QueueHttpTryElement>(), listLeftPushCommand.HttpStatusCodesWorthRetrying));
        else
            queues.Add(listLeftPushCommand.Key, new List<QueueElement>() {new(listLeftPushCommand.Value,listLeftPushCommand.Identifier, listLeftPushCommand.NowTicks, listLeftPushCommand.RetryTimeout, listLeftPushCommand.Retries,new List<QueueHttpTryElement>(), listLeftPushCommand.HttpStatusCodesWorthRetrying)});
        return default;
    }
    
    [CommandHandler]
    public ValueTask ListCallbackAsync(ListCallbackCommand addHashSetCommand, CancellationToken token)
    {
        return DoListCallbackAsync(addHashSetCommand, SlimDataState.Queues);
    }
    
    internal static ValueTask DoListCallbackAsync(ListCallbackCommand listCallbackCommand, Dictionary<string, List<QueueElement>> queues)
    {
        if (!queues.TryGetValue(listCallbackCommand.Key, out List<QueueElement>? value)) return default;
        
        var queueElement = value.FirstOrDefault(x => x.Id == listCallbackCommand.Identifier);
        if (queueElement == null)
        {
            return default;
        }

        if (listCallbackCommand.HttpCode == DeleteFromQueueCode)
        {
            value.Remove(queueElement);
        }
        else
        {
            var retryQueueElement = queueElement.RetryQueueElements[^1];
            retryQueueElement.EndTimeStamp = listCallbackCommand.NowTicks;
            retryQueueElement.HttpCode = listCallbackCommand.HttpCode;

            /*if (queueElement.IsFinished(listCallbackCommand.NowTicks))
            {
                value.Remove(queueElement);
            }*/
        }

        return default;
    }

    [CommandHandler]
    public ValueTask AddHashSetAsync(AddHashSetCommand addHashSetCommand, CancellationToken token)
    {
        return DoAddHashSetAsync(addHashSetCommand, SlimDataState.Hashsets);
    }
    
    internal static ValueTask DoAddHashSetAsync(AddHashSetCommand addHashSetCommand, Dictionary<string, Dictionary<string, string>> hashsets)
    {
        hashsets[addHashSetCommand.Key] = addHashSetCommand.Value;
        return default;
    }

    [CommandHandler]
    public ValueTask AddKeyValueAsync(AddKeyValueCommand valueCommand, CancellationToken token)
    {
        return DoAddKeyValueAsync(valueCommand, SlimDataState.KeyValues);
    }
    
    internal static ValueTask DoAddKeyValueAsync(AddKeyValueCommand valueCommand, Dictionary<string, ReadOnlyMemory<byte>> keyValues)
    {
        keyValues[valueCommand.Key] = valueCommand.Value;
        return default;
    }

    [CommandHandler(IsSnapshotHandler = true)]
    public ValueTask HandleSnapshotAsync(LogSnapshotCommand command, CancellationToken token)
    {
        DoHandleSnapshotAsync(command, SlimDataState.KeyValues, SlimDataState.Hashsets, SlimDataState.Queues);
        return default;
    }
    
    internal static ValueTask DoHandleSnapshotAsync(LogSnapshotCommand command, Dictionary<string, ReadOnlyMemory<byte>> keyValues, Dictionary<string, Dictionary<string, string>> hashsets, Dictionary<string, List<QueueElement>>  queues)
    {
        Console.WriteLine("Begin Handle SnapShot");
        keyValues.Clear();
        foreach (var keyValue in command.keysValues)
        {
            keyValues[keyValue.Key] = keyValue.Value;
        }
        
        queues.Clear();
        foreach (var queue in command.queues)
        {
            queues[queue.Key] = queue.Value;
        }
        
        hashsets.Clear();
        foreach (var hashset in command.hashsets)
        {
            hashsets[hashset.Key] = hashset.Value;
        }
        Console.WriteLine("End Handle SnapShot");
        return default;
    }   
    
    public static CommandInterpreter InitInterpreter(SlimDataState state)   
    {
        ValueTask ListRightPopHandler(ListRightPopCommand command, CancellationToken token) => DoListRightPopAsync(command, state.Queues);
        ValueTask ListLeftPushHandler(ListLeftPushCommand command, CancellationToken token) => DoListLeftPushAsync(command, state.Queues);
        ValueTask AddHashSetHandler(AddHashSetCommand command, CancellationToken token) => DoAddHashSetAsync(command, state.Hashsets);
        ValueTask AddKeyValueHandler(AddKeyValueCommand command, CancellationToken token) => DoAddKeyValueAsync(command, state.KeyValues);
        ValueTask ListSetQueueItemStatusAsync(ListCallbackCommand command, CancellationToken token) => DoListCallbackAsync(command, state.Queues);
        ValueTask SnapshotHandler(LogSnapshotCommand command, CancellationToken token) => DoHandleSnapshotAsync(command, state.KeyValues, state.Hashsets, state.Queues);

        var interpreter = new Builder()
            .Add(ListRightPopCommand.Id, (Func<ListRightPopCommand, CancellationToken, ValueTask>)ListRightPopHandler)
            .Add(ListLeftPushCommand.Id, (Func<ListLeftPushCommand, CancellationToken, ValueTask>)ListLeftPushHandler)
            .Add(AddHashSetCommand.Id, (Func<AddHashSetCommand, CancellationToken, ValueTask>)AddHashSetHandler)
            .Add(AddKeyValueCommand.Id, (Func<AddKeyValueCommand, CancellationToken, ValueTask>)AddKeyValueHandler)
            .Add(ListCallbackCommand.Id, (Func<ListCallbackCommand, CancellationToken, ValueTask>)ListSetQueueItemStatusAsync)
            .Add(LogSnapshotCommand.Id, (Func<LogSnapshotCommand, CancellationToken, ValueTask>)SnapshotHandler, true)
            .Build();

        return interpreter;
    }
    
    
}