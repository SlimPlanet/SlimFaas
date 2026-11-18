using System.Collections.Concurrent;
using System.Net;
using MemoryPack;
using Microsoft.Extensions.Logging;
using SlimData;
using SlimData.Commands;
using SlimFaas.Database;

namespace SlimFaas.Tests.Database;

public sealed partial class SlimDataRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queue_trace_links_retry_identity_reservation_callback_and_file_admission_without_changing_requests(bool enabled)
    {
        var logger = new QueueTraceLogger(enabled);
        var batches = new List<SlimDataCommandBatchRequest>();
        using var handler = new Handler(async (request, cancellation) =>
        {
            byte[] bytes = await request.Content!.ReadAsByteArrayAsync(cancellation);
            SlimDataCommandBatchRequest batch = MemoryPackSerializer.Deserialize<SlimDataCommandBatchRequest>(bytes)!;
            batches.Add(batch);
            if (batches.Count == 1)
                throw new HttpRequestException("Response lost after submission");
            SlimDataBatchOperation operation = Assert.Single(batch.Operations);
            var result = new SlimDataBatchOperationResult
            {
                Kind = operation.Kind, RequestId = operation.RequestId, Applied = true,
                ElementId = operation.ElementId
            };
            if (operation.Kind == SlimDataBatchOperationKind.ListRightPop)
                result.QueueItems = [new QueueData("element", [42], 1, true, 123, 456, "10.0.0.42")];
            if (operation.Kind == SlimDataBatchOperationKind.FileAdmission)
            {
                result.KeyValueResult = new KeyValueCommandResult();
                result.KeyValueResult.SetApplied(MemoryPackSerializer.Serialize(new FileAdmissionResult { Applied = true }));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MemoryPackSerializer.Serialize(new SlimDataCommandBatchResponse
                {
                    ProducerId = batch.ProducerId, Sequence = batch.Sequence, RequestId = batch.RequestId,
                    Duplicate = batches.Count == 2, Results = [result]
                }))
            };
        });
        await using var fixture = await Fixture.CreateAsync(handler, logger: logger);
        Assert.Equal("element", await fixture.Service.ListLeftPushAsync("Queue:function", [42],
            new RetryInformation([1], 8, [500]), "element").WaitAsync(TestDeadline));
        Assert.Equal("element", Assert.Single((await fixture.Service.ListRightPopAsync("Queue:function", "transaction"))!).Id);
        await fixture.Service.ListCallbackAsync("Queue:function", new ListQueueItemStatus
        {
            Items = [new QueueItemStatus("element", 200)]
        });
        await fixture.Service.HashSetAsync("ordinary-hash", new Dictionary<string, byte[]> { ["key"] = [1] });
        Assert.True((await fixture.Service.ExecuteFileAdmissionAsync(new FileAdmissionMutation
        {
            Action = FileAdmissionAction.CommitQueue, ElementId = "element", FileId = "file"
        })).Applied);

        Assert.Equal(6, batches.Count);
        Assert.Equal(batches[0].RequestId, batches[1].RequestId);
        Assert.Equal(batches[0].Operations[0].RequestId, batches[1].Operations[0].RequestId);
        Assert.Equal(batches[0].GenerationId, batches[1].GenerationId);
        Dictionary<string, object?>[] entries = logger.Entries.ToArray();
        if (!enabled)
        {
            Assert.Empty(entries);
            return;
        }
        Dictionary<string, object?>[] attempts = entries.Where(entry => entry.GetValueOrDefault("Stage") as string == "send-attempt" &&
            Equals(entry.GetValueOrDefault("Kind"), SlimDataBatchOperationKind.ListLeftPush)).ToArray();
        Assert.Equal([1, 2], attempts.Select(entry => (int)entry["BatchAttempt"]!));
        Assert.All(attempts, entry =>
        {
            Assert.Equal("element", entry["ElementId"]);
            Assert.Equal(batches[0].RequestId, entry["BatchRequestId"]);
            Assert.Equal(batches[0].Operations[0].RequestId, entry["OperationRequestId"]);
        });
        Assert.Contains(entries, entry => entry.GetValueOrDefault("Stage") as string == "response-validated" &&
            Equals(entry.GetValueOrDefault("DuplicateBatch"), true));
        Assert.Contains(entries, entry => Equals(entry.GetValueOrDefault("TryNumber"), 1) &&
            Equals(entry.GetValueOrDefault("ReservedTarget"), "10.0.0.42"));
        Assert.Contains(entries, entry => entry.GetValueOrDefault("Stage") as string == "response-validated" &&
            Equals(entry.GetValueOrDefault("ElementId"), "element") && Equals(entry.GetValueOrDefault("StatusCode"), 200));
        Assert.Contains(entries, entry => Equals(entry.GetValueOrDefault("ElementId"), "element") &&
            Equals(entry.GetValueOrDefault("FileId"), "file"));
    }

    private sealed class QueueTraceLogger(bool enabled) : ILogger<SlimDataService>
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
