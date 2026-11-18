using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed partial class SlimPersistentStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_batch_trace_preserves_operation_identity_and_duplicate_replay(bool enabled)
    {
        string root = GetTemporaryDirectory();
        var logger = new StateTraceLogger(enabled);
        try
        {
            await using (var state = new SlimPersistentState(root, logger))
            await using (var wal = await CreateRestoredWalAsync(state, Path.Combine(root, "wal")))
            {
                var request = new SlimDataCommandBatchRequest
                {
                    ProducerId = "trace-producer", GenerationId = "trace-generation", Sequence = 1,
                    RequestId = "trace-batch", Operations = [new SlimDataBatchOperation
                    {
                        Kind = SlimDataBatchOperationKind.ListLeftPush, RequestId = "trace-operation",
                        Key = "Queue:function", ElementId = "element", Value = [42],
                        NowTicks = DateTime.UtcNow.Ticks, RetryTimeoutSeconds = 8, Retries = [1], HttpStatusRetries = [500]
                    }]
                };
                var command = new ExecuteBatchCommand
                {
                    Payload = SlimDataRaftBatchEnvelopeCodec.Serialize(new SlimDataRaftBatchEnvelope { Requests = [request] })
                };
                var indices = new List<long>();
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    var context = new SlimDataCommandBatchContext();
                    var entry = await SerializedSlimDataLogEntry.CreateAsync(command, 4, context, CancellationToken.None);
                    long index = await wal.AppendAsync(entry, CancellationToken.None);
                    await wal.CommitAsync(index, CancellationToken.None);
                    await wal.WaitForApplyAsync(index, CancellationToken.None);
                    indices.Add(index);
                    Assert.Equal(repeat == 1, Assert.Single(context.Responses).Duplicate);
                    Assert.Equal("element", Assert.Single(state.SlimDataState.Queues["Queue:function"]).Id);
                }
                Dictionary<string, object?>[] records = logger.Entries.ToArray();
                if (enabled)
                {
                    Assert.Equal(indices, records.Select(record => (long)record["Index"]!));
                    Assert.Equal([false, true], records.Select(record => (bool)record["DuplicateBatch"]!));
                    Assert.All(records, record =>
                    {
                        Assert.Equal(4L, record["Term"]);
                        Assert.Equal("trace-producer", record["ProducerId"]);
                        Assert.Equal("trace-batch", record["BatchRequestId"]);
                        Assert.Equal("trace-operation", record["OperationRequestId"]);
                        Assert.Equal(true, record["Applied"]);
                    });
                }
                else
                {
                    Assert.Empty(records);
                }
            }

            // Replay has no local request context; trace absence must not be
            // interpreted as non-application or change durable queue deduplication.
            await using var replay = new SlimPersistentState(root);
            await using var replayWal = await CreateRestoredWalAsync(replay, Path.Combine(root, "wal"));
            await replayWal.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var replayTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await replayWal.WaitForApplyAsync(replayWal.LastCommittedEntryIndex, replayTimeout.Token);
            Assert.Equal("element", Assert.Single(replay.SlimDataState.Queues["Queue:function"]).Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StateTraceLogger(bool enabled) : ILogger<SlimPersistentState>
    {
        internal ConcurrentQueue<Dictionary<string, object?>> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => enabled && logLevel == LogLevel.Trace;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Trace && state is IEnumerable<KeyValuePair<string, object?>> values)
                Entries.Enqueue(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
    }
}
