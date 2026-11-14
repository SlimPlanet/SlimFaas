using System.Collections.Immutable;
using SlimData;
using SlimData.Commands;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Scaling;

public sealed class StreamScalingTests
{
    [Fact]
    public async Task Native_stream_work_wakes_scales_and_drains_without_exporter_or_observer_side_effects()
    {
        var fixture = new ScalingTestFixture();
        fixture.Config = new() { ReplicaMax = 5, Triggers = [] };
        await fixture.SetFunctions();
        var state = new SlimDataState(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
        fixture.StreamDemand.SetReader(() => new SlimDataPayload { KeyValues = state.KeyValues, Hashsets = state.Hashsets, Queues = state.Queues });
        DurableStreamMutationResult Apply(DurableStreamAction action, string element = "", int code = 200) =>
            DurableStreamStore.Apply(new()
            {
                Action = action, Stream = "orders", Function = fixture.Name, NewId = Guid.NewGuid().ToString("N"),
                NowTicks = fixture.Time.Now.UtcDateTime.Ticks, ElementId = element, StatusCode = code,
                Owner = "node", Replica = "replica", MaximumParallel = 100, MaximumPerReplica = 100
            }, state);
        Apply(DurableStreamAction.Configure); Apply(DurableStreamAction.Subscribe);
        for (int i = 0; i < 80; i++) Apply(DurableStreamAction.Append);
        long ticks = fixture.Http.GetTicksLastCall(fixture.Name);
        var preview = await fixture.Simulation.SimulateAsync(new(fixture.Name), default);
        Assert.Equal(0, fixture.Replicas.Deployments.Functions[0].Replicas);
        Assert.Equal(ticks, fixture.Http.GetTicksLastCall(fixture.Name));
        Assert.Contains(preview.Current.Reasons, reason => reason.Code == "StreamDemand");
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.InRange(fixture.Replicas.Deployments.Functions[0].Replicas, 1, 5);
        await fixture.Replicas.CheckScaleAsync("default");
        Assert.Equal(5, fixture.Replicas.Deployments.Functions[0].Replicas);
        for (int i = 0; i < 80; i++)
        {
            var attempt = Apply(DurableStreamAction.Reserve).Delivery!;
            Apply(DurableStreamAction.Complete, attempt.ElementId, i == 0 ? 400 : 200);
        }
        Assert.Equal(0, fixture.StreamDemand.Capture()[fixture.Name]);
        Apply(DurableStreamAction.Append);
        var deferred = Apply(DurableStreamAction.Reserve).Delivery!;
        Apply(DurableStreamAction.Complete, deferred.ElementId, 202);
        Apply(DurableStreamAction.Unsubscribe);
        Assert.Equal(1, fixture.StreamDemand.Capture()[fixture.Name]);
        Apply(DurableStreamAction.Complete, deferred.ElementId);
        Assert.False(fixture.StreamDemand.Capture().ContainsKey(fixture.Name));
        fixture.Time.Now = fixture.Time.Now.AddHours(1);
        for (int i = 0; i < 3; i++)
        {
            await fixture.Replicas.CheckScaleAsync("default");
            fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
        }
        Assert.Equal(0, fixture.Replicas.Deployments.Functions[0].Replicas);
    }
}
