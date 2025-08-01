﻿using System.Collections.Immutable;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using SlimData.Commands;

namespace SlimData;

public class SlimDataState(
    ImmutableDictionary<string, ImmutableDictionary<string, string>> Hashsets,
    ImmutableDictionary<string, ReadOnlyMemory<byte>> KeyValues,
    ImmutableDictionary<string, ImmutableList<QueueElement>> Queues)
{
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> Hashsets { get; set; } = Hashsets;
    public ImmutableDictionary<string, ReadOnlyMemory<byte>> KeyValues { get; set; } = KeyValues;
    public ImmutableDictionary<string, ImmutableList<QueueElement>> Queues { get; set; } = Queues;
}

public class QueueElement(
    ReadOnlyMemory<byte> value,
    string id,
    long insertTimeStamp,
    int httpTimeout,
    ImmutableList<int> timeoutRetries,
    ImmutableList<QueueHttpTryElement> retryQueueElements,
    ImmutableList<int> httpStatusRetries
)
{
    public ReadOnlyMemory<byte> Value { get; } = value;
    public string Id { get; } = id;
    public long InsertTimeStamp { get; } = insertTimeStamp;
    
    public ImmutableList<int> TimeoutRetries { get; } = timeoutRetries;

    public int HttpTimeout { get; } = httpTimeout;
    public ImmutableList<QueueHttpTryElement> RetryQueueElements { get; set; } = retryQueueElements;
    
    public ImmutableList<int> HttpStatusRetries { get; } = httpStatusRetries;
}

public class QueueHttpTryElement(long startTimeStamp = 0, string idTransaction = "", long endTimeStamp = 0, int httpCode = 0)
{
    public long StartTimeStamp { get; set; } = startTimeStamp;
    public long EndTimeStamp { get; set; } = endTimeStamp;
    public int HttpCode { get; set; } = httpCode;
    public string IdTransaction { get; set; } = idTransaction;
}

#pragma warning restore CA2252
public class SlimDataInterpreter : CommandInterpreter
{
    public const int DeleteFromQueueCode = 1000;

    public SlimDataState SlimDataState = new(
        ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableList<QueueElement>>.Empty
    );

    [CommandHandler]
    public ValueTask ListRightPopAsync(ListRightPopCommand addHashSetCommand, CancellationToken token)
    {
        return DoListRightPopAsync(addHashSetCommand, SlimDataState);
    }
    
    internal static ValueTask DoListRightPopAsync(ListRightPopCommand listRightPopCommand, SlimDataState slimDataState)
    {
        var queues = slimDataState.Queues;
        if (queues.TryGetValue(listRightPopCommand.Key, out var queue))
        {
            var nowTicks = listRightPopCommand.NowTicks;
            var queueTimeoutElements = queue.GetQueueTimeoutElement(nowTicks);
            foreach (var queueTimeoutElement in queueTimeoutElements)
            {
                var retryQueueElement = queueTimeoutElement.RetryQueueElements[^1];
                retryQueueElement.EndTimeStamp = nowTicks;
                retryQueueElement.HttpCode = 504;
            }

            var queueList = queue.ToList();
            var queueFinishedElements = queue.GetQueueFinishedElement(nowTicks);
            foreach (var queueFinishedElement in queueFinishedElements)
            {
                queueList.Remove(queueFinishedElement);
            }
            var isIdTransactionAlreadyExist = queue.Any(q => q.RetryQueueElements.Count > 0 && q.RetryQueueElements[^1].IdTransaction == listRightPopCommand.IdTransaction);
            if (!isIdTransactionAlreadyExist)
            {
                var queueAvailableElements = queue.GetQueueAvailableElement(nowTicks, listRightPopCommand.Count);
                foreach (var queueAvailableElement in queueAvailableElements)
                {
                    queueAvailableElement.RetryQueueElements =
                        queueAvailableElement.RetryQueueElements.Add(new QueueHttpTryElement(nowTicks,
                            listRightPopCommand.IdTransaction));
                }
            }

            slimDataState.Queues = queues.SetItem(listRightPopCommand.Key, ImmutableList.CreateRange(queueList));
        }
        
        return default;
    }

    [CommandHandler]
    public ValueTask ListLeftPushAsync(ListLeftPushCommand addHashSetCommand, CancellationToken token)
    {
        return DoListLeftPushAsync(addHashSetCommand, SlimDataState);
    }
    
    internal static ValueTask DoListLeftPushAsync(ListLeftPushCommand listLeftPushCommand, SlimDataState slimDataState)
    {
        var queues = slimDataState.Queues;
        var queueElement = new QueueElement(
            listLeftPushCommand.Value,
            listLeftPushCommand.Identifier,
            listLeftPushCommand.NowTicks,
            listLeftPushCommand.RetryTimeout,
            listLeftPushCommand.Retries.ToImmutableList(),
            ImmutableList<QueueHttpTryElement>.Empty,
            listLeftPushCommand.HttpStatusCodesWorthRetrying.ToImmutableList()
        );
        if (queues.TryGetValue(listLeftPushCommand.Key, out var value))
        {
            if (value.All(q => q.Id != listLeftPushCommand.Identifier))
            {
                var newValue = value.Add(queueElement);
                queues = queues.SetItem(listLeftPushCommand.Key, newValue);
            }
        }
        else
        {
            var newValue = ImmutableList<QueueElement>.Empty.Add(queueElement);
            queues = queues.Add(listLeftPushCommand.Key, newValue);
        }

        slimDataState.Queues = queues;

        return default;
    }
    
    [CommandHandler]
    public ValueTask ListCallbackAsync(ListCallbackCommand addHashSetCommand, CancellationToken token)
    {
        return DoListCallbackAsync(addHashSetCommand, SlimDataState);
    }
    
    internal static ValueTask DoListCallbackAsync(ListCallbackCommand listCallbackCommand, SlimDataState slimDataState)
    {
        var queues = slimDataState.Queues;
        if (!queues.TryGetValue(listCallbackCommand.Key, out var value)) return default;

        foreach (var callbackElement in listCallbackCommand.CallbackElements)
        {
            var queueElement = value.FirstOrDefault(x => x.Id == callbackElement.Identifier);
            if (queueElement == null) continue;

            if (callbackElement.HttpCode == DeleteFromQueueCode)
            {
                value = value.Remove(queueElement);
            }
            else if(queueElement.RetryQueueElements.Count > 0)
            {
                var retryQueueElement = queueElement.RetryQueueElements[^1];
                retryQueueElement.EndTimeStamp = listCallbackCommand.NowTicks;
                retryQueueElement.HttpCode = callbackElement.HttpCode;

                if (queueElement.IsFinished(listCallbackCommand.NowTicks))
                {
                    value = value.Remove(queueElement);
                }
            }
        }
        
        slimDataState.Queues = queues.SetItem(listCallbackCommand.Key, value);

        return default;
    }

    [CommandHandler]
    public ValueTask AddHashSetAsync(AddHashSetCommand addHashSetCommand, CancellationToken token)
    {
        return DoAddHashSetAsync(addHashSetCommand, SlimDataState);
    }
    
    internal static ValueTask DoAddHashSetAsync(AddHashSetCommand addHashSetCommand, SlimDataState slimDataState)
    {
        var hashsets = slimDataState.Hashsets;
        slimDataState.Hashsets = hashsets.SetItem(addHashSetCommand.Key, addHashSetCommand.Value.ToImmutableDictionary());
        return default;
    }

    [CommandHandler]
    public ValueTask AddKeyValueAsync(AddKeyValueCommand valueCommand, CancellationToken token)
    {
        return DoAddKeyValueAsync(valueCommand, SlimDataState);
    }
    
    internal static ValueTask DoAddKeyValueAsync(AddKeyValueCommand valueCommand, SlimDataState slimDataState)
    {
        var keyValues = slimDataState.KeyValues;
        slimDataState.KeyValues = keyValues.SetItem(valueCommand.Key, valueCommand.Value);
        return default;
    }

    [CommandHandler(IsSnapshotHandler = true)]
    public ValueTask HandleSnapshotAsync(LogSnapshotCommand command, CancellationToken token)
    {
        DoHandleSnapshotAsync(command, SlimDataState);
        return default;
    }
    
    internal static ValueTask DoHandleSnapshotAsync(LogSnapshotCommand command, SlimDataState slimDataState)
    {
        slimDataState.KeyValues = command.keysValues.ToImmutableDictionary();
        
        var queues = ImmutableDictionary<string, ImmutableList<QueueElement>>.Empty;
        foreach (var queue in command.queues)
        {
            queues = queues.SetItem(queue.Key, queue.Value.ToImmutableList());
        }
        slimDataState.Queues = queues;
        
        var hashsets = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
        foreach (var hashset in command.hashsets)
        {
            hashsets = hashsets.SetItem(hashset.Key, hashset.Value.ToImmutableDictionary());
        }
        slimDataState.Hashsets = hashsets;
        
        return default;
    }   
    
    public static CommandInterpreter InitInterpreter(SlimDataState state)   
    {
        ValueTask ListRightPopHandler(ListRightPopCommand command, CancellationToken token) => DoListRightPopAsync(command, state);
        ValueTask ListLeftPushHandler(ListLeftPushCommand command, CancellationToken token) => DoListLeftPushAsync(command, state);
        ValueTask AddHashSetHandler(AddHashSetCommand command, CancellationToken token) => DoAddHashSetAsync(command, state);
        ValueTask AddKeyValueHandler(AddKeyValueCommand command, CancellationToken token) => DoAddKeyValueAsync(command, state);
        ValueTask ListSetQueueItemStatusAsync(ListCallbackCommand command, CancellationToken token) => DoListCallbackAsync(command, state);
        ValueTask SnapshotHandler(LogSnapshotCommand command, CancellationToken token) => DoHandleSnapshotAsync(command, state);

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
