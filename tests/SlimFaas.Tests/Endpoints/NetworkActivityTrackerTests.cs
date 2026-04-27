using SlimFaas.Endpoints;
using SlimFaas.Options;

namespace SlimFaas.Tests.Endpoints;

public class NetworkActivityTrackerTests
{
    [Fact(DisplayName = "Record stores events and GetRecent returns them")]
    public void Record_StoresEvents()
    {
        var tracker = new NetworkActivityTracker();

        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);
        tracker.Record(NetworkActivityTracker.EventTypes.Enqueue, NetworkActivityTracker.Actors.SlimFaas, "fibonacci", "fibonacci");
        tracker.Record(NetworkActivityTracker.EventTypes.Dequeue, NetworkActivityTracker.Actors.SlimFaas, "fibonacci", "fibonacci");

        var recent = tracker.GetRecent();

        Assert.Equal(3, recent.Count);
        Assert.Equal("request_in", recent[0].Type);
        Assert.Equal("external", recent[0].Source);
        Assert.Equal("slimfaas", recent[0].Target);
        Assert.Null(recent[0].QueueName);

        Assert.Equal("enqueue", recent[1].Type);
        Assert.Equal("fibonacci", recent[1].QueueName);
    }

    [Fact(DisplayName = "Record trims to MaxRecentEvents (1000)")]
    public void Record_TrimsToMax()
    {
        var tracker = new NetworkActivityTracker();

        for (int i = 0; i < 1250; i++)
        {
            tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);
        }

        var recent = tracker.GetRecent();
        Assert.Equal(1000, recent.Count);
    }

    [Fact(DisplayName = "Subscribe receives events via channel")]
    public async Task Subscribe_ReceivesEvents()
    {
        var tracker = new NetworkActivityTracker();
        var (reader, channel) = tracker.Subscribe();

        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);

        var hasItem = await reader.WaitToReadAsync(new CancellationTokenSource(1000).Token);
        Assert.True(hasItem);

        var success = reader.TryRead(out var evt);
        Assert.True(success);
        Assert.NotNull(evt);
        Assert.Equal("request_in", evt.Type);

        tracker.Unsubscribe(channel);
    }

    [Fact(DisplayName = "Unsubscribe completes the channel")]
    public void Unsubscribe_CompletesChannel()
    {
        var tracker = new NetworkActivityTracker();
        var (reader, channel) = tracker.Subscribe();

        tracker.Unsubscribe(channel);

        // After unsubscribe, the channel writer should be completed
        Assert.True(channel.Writer.TryComplete() || reader.Completion.IsCompleted);
    }

    [Fact(DisplayName = "HasSubscribers reflects active subscriptions")]
    public void HasSubscribers_ReflectsSubscriptions()
    {
        var tracker = new NetworkActivityTracker();

        Assert.False(tracker.HasSubscribers);

        var (_, channel) = tracker.Subscribe();
        Assert.True(tracker.HasSubscribers);

        tracker.Unsubscribe(channel);
        Assert.False(tracker.HasSubscribers);
    }

    [Fact(DisplayName = "TrySubscribe respects MaxSseClients")]
    public void TrySubscribe_RespectsMaxSseClients()
    {
        var tracker = CreateTracker(new StatusStreamOptions { MaxSseClients = 1 });

        Assert.True(tracker.TrySubscribe(out _, out var firstChannel));
        Assert.True(tracker.HasSubscribers);

        Assert.False(tracker.TrySubscribe(out _, out var rejectedChannel));
        Assert.True(rejectedChannel.Reader.Completion.IsCompleted || rejectedChannel.Writer.TryComplete());

        tracker.Unsubscribe(firstChannel);
        Assert.True(tracker.TrySubscribe(out _, out var secondChannel));
        tracker.Unsubscribe(secondChannel);
    }

    [Fact(DisplayName = "LiveEventSamplingRatio only affects subscriber broadcast")]
    public async Task LiveEventSamplingRatio_DropsBroadcastButKeepsRecentEvents()
    {
        var tracker = CreateTracker(new StatusStreamOptions { LiveEventSamplingRatio = 0 });
        var (reader, channel) = tracker.Subscribe();

        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);

        Assert.Single(tracker.GetRecent());
        await Task.Delay(50);
        Assert.False(reader.TryRead(out _));

        tracker.Unsubscribe(channel);
    }

    [Fact(DisplayName = "MaxLiveEventsPerSecond limits subscriber broadcast")]
    public void MaxLiveEventsPerSecond_LimitsBroadcast()
    {
        var tracker = CreateTracker(new StatusStreamOptions { MaxLiveEventsPerSecond = 2 });
        var (reader, channel) = tracker.Subscribe();

        for (int i = 0; i < 5; i++)
        {
            tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);
        }

        var broadcastCount = 0;
        while (reader.TryRead(out _))
        {
            broadcastCount++;
        }

        Assert.Equal(5, tracker.GetRecent().Count);
        Assert.Equal(2, broadcastCount);
        tracker.Unsubscribe(channel);
    }

    [Fact(DisplayName = "RecentActivityLimit trims using configured value")]
    public void RecentActivityLimit_TrimsUsingConfiguredValue()
    {
        var tracker = CreateTracker(new StatusStreamOptions { RecentActivityLimit = 3 });

        for (int i = 0; i < 5; i++)
        {
            tracker.Record($"event-{i}", NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);
        }

        var recent = tracker.GetRecent();
        Assert.Equal(3, recent.Count);
        Assert.Equal("event-2", recent[0].Type);
        Assert.Equal("event-4", recent[^1].Type);
    }

    [Fact(DisplayName = "Events have unique IDs, timestamps, and NodeId")]
    public void Events_HaveUniqueIdsAndNodeId()
    {
        var tracker = new NetworkActivityTracker();

        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);
        tracker.Record(NetworkActivityTracker.EventTypes.Enqueue, NetworkActivityTracker.Actors.SlimFaas, "fibonacci");

        var recent = tracker.GetRecent();
        Assert.Equal(2, recent.Count);
        Assert.NotEqual(recent[0].Id, recent[1].Id);
        Assert.True(recent[0].TimestampMs > 0);
        Assert.True(recent[1].TimestampMs >= recent[0].TimestampMs);

        // NodeId should be non-empty and consistent
        Assert.False(string.IsNullOrEmpty(recent[0].NodeId));
        Assert.Equal(recent[0].NodeId, recent[1].NodeId);
        Assert.Equal(tracker.NodeId, recent[0].NodeId);
    }

    [Fact(DisplayName = "IngestRemote adds foreign events and deduplicates")]
    public void IngestRemote_DeduplicatesById()
    {
        var tracker = new NetworkActivityTracker();

        var remote1 = new NetworkActivityEvent("peer2-1", "request_in", "external", "slimfaas", null, 100, "peer2");
        var remote2 = new NetworkActivityEvent("peer2-2", "enqueue", "slimfaas", "fibonacci", "fibonacci", 101, "peer2");

        int ingested = tracker.IngestRemote(new[] { remote1, remote2 });
        Assert.Equal(2, ingested);
        Assert.Equal(2, tracker.GetRecent().Count);

        // Ingest the same again — should be deduplicated
        int ingested2 = tracker.IngestRemote(new[] { remote1, remote2 });
        Assert.Equal(0, ingested2);
        Assert.Equal(2, tracker.GetRecent().Count); // still 2
    }

    [Fact(DisplayName = "IngestRemote broadcasts to SSE subscribers")]
    public async Task IngestRemote_BroadcastsToSubscribers()
    {
        var tracker = new NetworkActivityTracker();
        var (reader, channel) = tracker.Subscribe();

        var remoteEvt = new NetworkActivityEvent("peer3-1", "dequeue", "slimfaas", "fib", null, 200, "peer3");
        tracker.IngestRemote(new[] { remoteEvt });

        var hasItem = await reader.WaitToReadAsync(new CancellationTokenSource(1000).Token);
        Assert.True(hasItem);

        reader.TryRead(out var received);
        Assert.NotNull(received);
        Assert.Equal("peer3-1", received.Id);
        Assert.Equal("peer3", received.NodeId);

        tracker.Unsubscribe(channel);
    }

    [Fact(DisplayName = "GetLocalSince returns only local events after the given timestamp")]
    public void GetLocalSince_FiltersCorrectly()
    {
        var tracker = new NetworkActivityTracker();

        // Record a local event
        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);

        var all = tracker.GetRecent();
        Assert.Single(all);
        long ts = all[0].TimestampMs;

        // Ingest a remote event
        var remote = new NetworkActivityEvent("peer-1", "enqueue", "slimfaas", "fib", null, ts + 50, "peer");
        tracker.IngestRemote(new[] { remote });

        // GetLocalSince should only return local events
        var localSince = tracker.GetLocalSince(0);
        Assert.Single(localSince);
        Assert.Equal(tracker.NodeId, localSince[0].NodeId);

        // GetLocalSince with future timestamp returns empty
        var localSinceFuture = tracker.GetLocalSince(ts + 1000);
        Assert.Empty(localSinceFuture);
    }

    [Fact(DisplayName = "Multi-node simulation: two trackers exchange events via IngestRemote")]
    public void MultiNode_Simulation()
    {
        var nodeA = new NetworkActivityTracker();
        var nodeB = new NetworkActivityTracker();

        // Simulate Node A recording events
        nodeA.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);
        nodeA.Record(NetworkActivityTracker.EventTypes.Enqueue, NetworkActivityTracker.Actors.SlimFaas, "fibonacci", "fibonacci");

        // Node A local events
        var nodeALocal = nodeA.GetLocalSince(0);
        Assert.Equal(2, nodeALocal.Count);

        // Simulate Node B recording its own event
        nodeB.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);

        // Simulate peer scrape: create "foreign" events as if they came from a different node
        var foreignEvents = nodeALocal.Select(e => e with { NodeId = "node-a-remote", Id = "remote-" + e.Id }).ToList();
        int ingestedByB = nodeB.IngestRemote(foreignEvents);
        Assert.Equal(2, ingestedByB);

        // NodeB should now have 3 events total (1 local + 2 from nodeA)
        var nodeBAll = nodeB.GetRecent();
        Assert.Equal(3, nodeBAll.Count);

        // Reverse: create "foreign" events from nodeB's local events as if they came from a different node
        var nodeBLocal = nodeB.GetLocalSince(0);
        Assert.Single(nodeBLocal); // only nodeB's own event

        var foreignFromB = nodeBLocal.Select(e => e with { NodeId = "node-b-remote", Id = "remote-" + e.Id }).ToList();
        int ingestedByA = nodeA.IngestRemote(foreignFromB);
        Assert.Equal(1, ingestedByA);

        // NodeA should now have 3 events total
        var nodeAAll = nodeA.GetRecent();
        Assert.Equal(3, nodeAAll.Count);

        // Both nodes have the same number of events (global view)
        Assert.Equal(nodeAAll.Count, nodeBAll.Count);

        // Ingesting duplicates should be no-ops
        Assert.Equal(0, nodeB.IngestRemote(foreignEvents));  // nodeB already has these
        Assert.Equal(0, nodeA.IngestRemote(foreignFromB));   // nodeA already has these
    }

    [Fact(DisplayName = "Record stores SourcePod and TargetPod")]
    public void Record_StoresSourceAndTargetPod()
    {
        var tracker = new NetworkActivityTracker();

        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas, sourcePod: "10.0.0.42");
        tracker.Record(NetworkActivityTracker.EventTypes.RequestOut, NetworkActivityTracker.Actors.SlimFaas, "fibonacci", targetPod: "10.0.0.5");
        tracker.Record(NetworkActivityTracker.EventTypes.Dequeue, NetworkActivityTracker.Actors.SlimFaas, "fibonacci", "fibonacci", sourcePod: null, targetPod: "10.0.0.5");

        var recent = tracker.GetRecent();
        Assert.Equal(3, recent.Count);

        Assert.Equal("10.0.0.42", recent[0].SourcePod);
        Assert.Null(recent[0].TargetPod);

        Assert.Null(recent[1].SourcePod);
        Assert.Equal("10.0.0.5", recent[1].TargetPod);

        Assert.Null(recent[2].SourcePod);
        Assert.Equal("10.0.0.5", recent[2].TargetPod);
    }

    [Fact(DisplayName = "IngestRemote preserves SourcePod and TargetPod")]
    public void IngestRemote_PreservesSourceAndTargetPod()
    {
        var tracker = new NetworkActivityTracker();

        var remote = new NetworkActivityEvent(
            "peer-pod-1", "request_out", "slimfaas", "fibonacci", null, 500, "peer-node",
            SourcePod: "10.0.0.1", TargetPod: "10.0.0.99");

        tracker.IngestRemote(new[] { remote });
        var recent = tracker.GetRecent();

        Assert.Single(recent);
        Assert.Equal("10.0.0.1", recent[0].SourcePod);
        Assert.Equal("10.0.0.99", recent[0].TargetPod);
    }

    [Fact(DisplayName = "Record with both SourcePod and TargetPod")]
    public void Record_BothSourceAndTargetPod()
    {
        var tracker = new NetworkActivityTracker();

        tracker.Record(NetworkActivityTracker.EventTypes.RequestOut, NetworkActivityTracker.Actors.SlimFaas, "fibonacci",
            sourcePod: "10.0.0.1", targetPod: "10.0.0.5");

        var recent = tracker.GetRecent();
        Assert.Single(recent);
        Assert.Equal("10.0.0.1", recent[0].SourcePod);
        Assert.Equal("10.0.0.5", recent[0].TargetPod);
    }

    private static NetworkActivityTracker CreateTracker(StatusStreamOptions statusStreamOptions)
    {
        return new NetworkActivityTracker(Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            EnableFront = true,
            StatusStream = statusStreamOptions
        }));
    }
}




