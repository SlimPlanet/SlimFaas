using System.Diagnostics;
using System.Threading.Channels;
using DotNext.Net.Cluster.Consensus.Raft;

namespace SlimData;

public readonly record struct SlimDataCommandBatchCoordinatorStatistics(
    int QueueRequests,
    long QueueBytes,
    long RaftBatches,
    long ProducerBatches,
    long Operations,
    long Duplicates,
    long SequenceGaps,
    double LastRaftLatencyMilliseconds,
    double LastMaxQueueWaitMilliseconds,
    int LastProducerBatchCount,
    int LastOperationCount,
    long LastPayloadBytes);

public sealed class SlimDataCommandBatchCoordinator : IAsyncDisposable
{
    private const int Capacity = 256;
    private const int MaxAggregateRequestBytes = 15 * 1024 * 1024;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(2);

    private readonly IRaftCluster _cluster;
    private readonly ILogger<SlimDataCommandBatchCoordinator> _logger;
    private readonly Channel<PendingRequest> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _queueRequests;
    private long _queueBytes;
    private long _raftBatches;
    private long _producerBatches;
    private long _operations;
    private long _duplicates;
    private long _sequenceGaps;
    private long _lastRaftLatencyMicroseconds;
    private long _lastMaxQueueWaitMicroseconds;
    private int _lastProducerBatchCount;
    private int _lastOperationCount;
    private long _lastPayloadBytes;

    public SlimDataCommandBatchCoordinator(
        IRaftCluster cluster,
        ILogger<SlimDataCommandBatchCoordinator> logger)
    {
        _cluster = cluster;
        _logger = logger;
        _channel = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessAsync);
    }

    public async Task<SlimDataCommandBatchResponse> EnqueueAsync(
        SlimDataCommandBatchRequest request,
        int requestBytes,
        CancellationToken cancellationToken)
    {
        SlimDataCommandBatchValidator.ValidateRequest(request);
        var pending = new PendingRequest(request, Math.Max(requestBytes, 0));
        if (!_channel.Writer.TryWrite(pending))
            throw new TooManyRequestsException();

        Interlocked.Increment(ref _queueRequests);
        Interlocked.Add(ref _queueBytes, pending.RequestBytes);
        return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public SlimDataCommandBatchCoordinatorStatistics GetStatistics()
        => new(
            Volatile.Read(ref _queueRequests),
            Volatile.Read(ref _queueBytes),
            Volatile.Read(ref _raftBatches),
            Volatile.Read(ref _producerBatches),
            Volatile.Read(ref _operations),
            Volatile.Read(ref _duplicates),
            Volatile.Read(ref _sequenceGaps),
            Volatile.Read(ref _lastRaftLatencyMicroseconds) / 1000d,
            Volatile.Read(ref _lastMaxQueueWaitMicroseconds) / 1000d,
            Volatile.Read(ref _lastProducerBatchCount),
            Volatile.Read(ref _lastOperationCount),
            Volatile.Read(ref _lastPayloadBytes));

    private async Task ProcessAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (!_channel.Reader.TryRead(out var first))
                    continue;

                var batch = new List<PendingRequest>(SlimDataCommandBatchValidator.MaxRequestsPerRaftBatch)
                {
                    first
                };
                var payloadBytes = first.RequestBytes;

                await Task.Delay(CoalesceWindow, _shutdown.Token).ConfigureAwait(false);
                while (batch.Count < SlimDataCommandBatchValidator.MaxRequestsPerRaftBatch &&
                       _channel.Reader.TryPeek(out var next) &&
                       payloadBytes + next.RequestBytes <= MaxAggregateRequestBytes &&
                       _channel.Reader.TryRead(out next))
                {
                    batch.Add(next);
                    payloadBytes += next.RequestBytes;
                }

                RemoveFromQueueStatistics(batch, payloadBytes);
                await ReplicateAsync(batch, payloadBytes).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The centralized SlimData command batch coordinator stopped unexpectedly");
            FailPending(ex);
        }
        finally
        {
            FailPending(new ObjectDisposedException(nameof(SlimDataCommandBatchCoordinator)));
        }
    }

    private async Task ReplicateAsync(List<PendingRequest> batch, int payloadBytes)
    {
        var envelope = new SlimDataRaftBatchEnvelope
        {
            Requests = batch.Select(static pending => pending.Request).ToArray()
        };
        var operationCount = envelope.Requests.Sum(static request => request.Operations.Length);
        var maxQueueWait = batch.Max(static pending =>
            Stopwatch.GetElapsedTime(pending.EnqueuedTimestamp).TotalMilliseconds);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var responses = await Endpoints.ExecuteCommandBatchCommandAsync(
                    envelope,
                    _cluster,
                    _shutdown.Token)
                .ConfigureAwait(false);
            if (responses.Length != batch.Count)
                throw new InvalidDataException("The composite RAFT response count does not match the producer batch count.");

            Interlocked.Increment(ref _raftBatches);
            Interlocked.Add(ref _producerBatches, batch.Count);
            Interlocked.Add(ref _operations, operationCount);
            Interlocked.Add(ref _duplicates, responses.LongCount(static response => response.Duplicate));
            Interlocked.Add(ref _sequenceGaps, responses.LongCount(static response => response.SequenceGap));
            Volatile.Write(
                ref _lastRaftLatencyMicroseconds,
                (long)(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds * 1000d));
            Volatile.Write(ref _lastMaxQueueWaitMicroseconds, (long)(maxQueueWait * 1000d));
            Volatile.Write(ref _lastProducerBatchCount, batch.Count);
            Volatile.Write(ref _lastOperationCount, operationCount);
            Volatile.Write(ref _lastPayloadBytes, payloadBytes);

            for (var i = 0; i < batch.Count; i++)
                batch[i].Completion.TrySetResult(responses[i]);
        }
        catch (Exception ex)
        {
            foreach (var pending in batch)
                pending.Completion.TrySetException(ex);
        }
    }

    private void RemoveFromQueueStatistics(List<PendingRequest> batch, int payloadBytes)
    {
        Interlocked.Add(ref _queueRequests, -batch.Count);
        Interlocked.Add(ref _queueBytes, -payloadBytes);
    }

    private void FailPending(Exception exception)
    {
        while (_channel.Reader.TryRead(out var pending))
        {
            Interlocked.Decrement(ref _queueRequests);
            Interlocked.Add(ref _queueBytes, -pending.RequestBytes);
            pending.Completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown.Dispose();
    }

    private sealed class PendingRequest(SlimDataCommandBatchRequest request, int requestBytes)
    {
        internal SlimDataCommandBatchRequest Request { get; } = request;
        internal int RequestBytes { get; } = requestBytes;
        internal long EnqueuedTimestamp { get; } = Stopwatch.GetTimestamp();
        internal TaskCompletionSource<SlimDataCommandBatchResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
