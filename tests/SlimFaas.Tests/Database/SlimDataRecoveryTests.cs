using System.Collections.Concurrent;
using System.Net;
using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using MemoryPack;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Options;

namespace SlimFaas.Tests.Database;

public sealed class SlimDataRecoveryTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(12);

    [Theory]
    [InlineData(SlimDataBatchMode.Global)]
    [InlineData(SlimDataBatchMode.PartitionedByKey)]
    public async Task Accepted_job_is_dequeued_after_response_body_timeout_and_leader_change(SlimDataBatchMode mode)
    {
        using var stalled = new StalledContent();
        var requests = new ConcurrentQueue<(Uri Uri, byte[] Payload)>();
        var popAttempts = 0;
        var acceptedJobId = string.Empty;
        using var handler = new Handler(async (request, token) =>
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            requests.Enqueue((request.RequestUri!, bytes));
            var batch = MemoryPackSerializer.Deserialize<SlimDataCommandBatchRequest>(bytes)!;
            var operation = Assert.Single(batch.Operations);
            if (operation.Kind == SlimDataBatchOperationKind.ListLeftPush)
                acceptedJobId = operation.ElementId;
            // The leader has committed this dequeue, but its first response stalls.
            if (operation.Kind == SlimDataBatchOperationKind.ListRightPop &&
                Interlocked.Increment(ref popAttempts) == 1)
                return new(HttpStatusCode.OK) { Content = stalled };
            return Reply(batch, acceptedJobId);
        });
        await using var fixture = new Fixture(handler, TimeSpan.FromSeconds(1), mode);
        var queue = new JobQueue(fixture.Service);
        var acceptedId = await queue.EnqueueAsync("recovery", [42]).WaitAsync(TestDeadline);
        var dequeue = queue.DequeueAsync("recovery");
        await stalled.Started.Task.WaitAsync(TestDeadline);
        fixture.LeaderPort = 3263;
        // A later mutation must stay behind the unresolved dequeue.
        var nextJob = queue.EnqueueAsync("recovery", [43]);

        var jobs = await dequeue.WaitAsync(TestDeadline);
        await nextJob.WaitAsync(TestDeadline);

        Assert.Equal(acceptedId, Assert.Single(jobs!).Id);
        Assert.False(string.IsNullOrEmpty(acceptedId));
        Assert.True(stalled.Disposed);
        var sent = requests.ToArray();
        Assert.Equal(4, sent.Length);
        Assert.Equal(3262, sent[1].Uri.Port);
        Assert.Equal(3263, sent[2].Uri.Port);
        Assert.Equal(sent[1].Payload, sent[2].Payload);
        var before = MemoryPackSerializer.Deserialize<SlimDataCommandBatchRequest>(sent[1].Payload)!;
        var after = MemoryPackSerializer.Deserialize<SlimDataCommandBatchRequest>(sent[3].Payload)!;
        Assert.Equal(before.Sequence + 1, after.Sequence);
        Assert.Equal(before.ProducerId, after.ProducerId);
        Assert.Equal(before.GenerationId, after.GenerationId);
        Assert.NotEqual(before.RequestId, after.RequestId);
    }

    [Fact]
    public async Task Shutdown_cancels_stalled_body_and_queued_mutations_without_retrying()
    {
        using var stalled = new StalledContent();
        var attempts = 0;
        using var handler = new Handler((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = stalled });
        });
        await using var fixture = new Fixture(handler, Timeout.InfiniteTimeSpan);
        var queue = new JobQueue(fixture.Service);
        var first = queue.EnqueueAsync("recovery", [1]);
        await stalled.Started.Task.WaitAsync(TestDeadline);
        var second = queue.EnqueueAsync("recovery", [2]);

        await fixture.Service.DisposeAsync().AsTask().WaitAsync(TestDeadline);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestDeadline));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TestDeadline));
        Assert.True(stalled.Disposed);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("elements")]
    [InlineData("count")]
    [InlineData("dispatch")]
    [InlineData("hash")]
    public async Task Read_retries_after_local_apply_timeout_and_recovers(string readKind)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Read must not mutate"));
        await using var fixture = new Fixture(handler);
        using var cleanup = new CancellationTokenSource();
        var attempts = 0;
        fixture.Log.WaitForApply = token =>
            Interlocked.Increment(ref attempts) == 1 ? StallApplyAsync(token, cleanup.Token) : ValueTask.CompletedTask;

        var read = ReadAsync(fixture.Service, readKind);
        try
        {
            await read.WaitAsync(TestDeadline);
            Assert.Equal(2, attempts);
        }
        finally
        {
            await cleanup.CancelAsync();
            try { await read.WaitAsync(TestDeadline); }
            catch (Exception) { /* Observe failures after releasing the test's stalled operation. */ }
        }
    }

    [Fact]
    public async Task Persistent_local_apply_timeout_is_reported_as_unavailable()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Read must not mutate"));
        await using var fixture = new Fixture(handler);
        using var cleanup = new CancellationTokenSource();
        fixture.Log.WaitForApply = token => StallApplyAsync(token, cleanup.Token);

        try
        {
            var error = await Assert.ThrowsAsync<SlimDataUnavailableException>(() =>
                fixture.Service.ListCountAsync("Job:recovery", [CountType.Available])
                    .WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Contains("local", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await cleanup.CancelAsync();
        }
    }

    private static async ValueTask StallApplyAsync(CancellationToken token, CancellationToken cleanup)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cleanup);
        await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
    }

    private static async Task ReadAsync(SlimDataService service, string kind)
    {
        switch (kind)
        {
            case "elements": Assert.Empty(await service.ListCountElementAsync("Job:recovery", [CountType.Available])); break;
            case "count": Assert.Equal(0, await service.ListCountAsync("Job:recovery", [CountType.Available])); break;
            case "dispatch": Assert.Equal(QueueDispatchState.Empty, await service.GetQueueDispatchStateAsync("Job:recovery")); break;
            case "hash": Assert.Empty(await service.HashGetAllAsync("recovery")); break;
        }
    }

    private static HttpResponseMessage Reply(SlimDataCommandBatchRequest request, string acceptedJobId)
    {
        var operation = Assert.Single(request.Operations);
        var response = new SlimDataCommandBatchResponse
        {
            ProducerId = request.ProducerId,
            Sequence = request.Sequence,
            RequestId = request.RequestId,
            Results = [new SlimDataBatchOperationResult
            {
                Kind = operation.Kind, RequestId = operation.RequestId, Applied = true,
                ElementId = operation.ElementId,
                QueueItems = [new QueueData(acceptedJobId, [42], 1, false, 0, 0, "")]
            }]
        };
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(MemoryPackSerializer.Serialize(response)) };
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        private readonly SlimPersistentState _state;
        private readonly ServiceProvider _services;
        private readonly HttpClient _client;
        public Mock<IRaftCluster> Cluster { get; } = new();
        public ControlledLog Log { get; }
        public int LeaderPort { get; set; } = 3262;
        public SlimDataService Service { get; }

        public Fixture(HttpMessageHandler handler, TimeSpan? timeout = null, SlimDataBatchMode mode = SlimDataBatchMode.Global)
        {
            Directory.CreateDirectory(_directory);
            _state = new SlimPersistentState(_directory);
            Log = new ControlledLog(new WriteAheadLog.Options { Location = Path.Combine(_directory, "wal") }, _state);
            _services = new ServiceCollection().AddSingleton(_state).BuildServiceProvider();
            var member = new Mock<IRaftClusterMember>();
            member.SetupGet(m => m.EndPoint).Returns(() => new UriEndPoint(new Uri($"http://127.0.0.1:{LeaderPort}")));
            Cluster.SetupGet(c => c.Leader).Returns(member.Object);
            Cluster.SetupGet(c => c.AuditTrail).Returns(Log);
            // Follower reads do not need a leader lease after local application.
            Cluster.SetupGet(c => c.LeadershipToken).Returns(new CancellationToken(true));
            var compatibility = new Mock<ISlimDataProtocolCompatibility>();
            compatibility.SetupGet(c => c.IsCompatible).Returns(true);
            _client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(SlimDataService.HttpClientName)).Returns(_client);
            Service = new SlimDataService(factory.Object, _services, Cluster.Object, compatibility.Object,
                new SlimDataInfo(3262), Microsoft.Extensions.Options.Options.Create(new SlimDataOptions { BatchMode = mode }),
                NullLogger<SlimDataService>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            _client.Dispose();
            await _services.DisposeAsync();
            await Log.DisposeAsync();
            await _state.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    // Keep DotNext's persistent-state implementation (its internal setter cannot
    // be proxied by Castle), overriding only the local-application observation.
    private sealed class ControlledLog(WriteAheadLog.Options options, IStateMachine state)
        : WriteAheadLog(options, state), IAuditTrail
    {
        public Func<CancellationToken, ValueTask> WaitForApply { get; set; } = _ => ValueTask.CompletedTask;
        long IAuditTrail.LastCommittedEntryIndex => 7L;
        ValueTask IAuditTrail.WaitForApplyAsync(long index, CancellationToken token)
        {
            Assert.Equal(7L, index);
            return WaitForApply(token);
        }
        ValueTask<TResult> IAuditTrail.ReadAsync<TResult>(ILogEntryConsumer<ILogEntry, TResult> reader,
            long startIndex, long endIndex, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class StalledContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
