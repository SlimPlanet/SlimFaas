using System.Collections.Immutable;
using System.Text;
using DotNext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas;
using Xunit;

public sealed class DataSetRoutesTests
{
    private const string TtlSuffix = "${slimfaas-timetolive}$";

    [Fact]
    public async Task Post_sets_value_and_returns_id()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var body = Encoding.UTF8.GetBytes("hello");
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(body);

        db.Setup(d => d.SetAsync("data:set:element1", It.Is<byte[]>(b => b.SequenceEqual(body)), 123L))
          .Returns(Task.CompletedTask);

        var res = await DataSetRoutes.Handlers.PostAsync(ctx, db.Object, "element1", 123L, CancellationToken.None);

        var ok = Assert.IsType<Ok<string>>(res);
        Assert.Equal("element1", ok.Value);

        db.VerifyAll();
    }

    [Fact]
    public async Task Get_returns_notfound_when_missing()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        db.Setup(d => d.GetAsync("data:set:missing"))
          .ReturnsAsync((byte[]?)null);

        var res = await DataSetRoutes.Handlers.GetAsync(db.Object, "missing");

        Assert.IsType<NotFound>(res);
        db.VerifyAll();
    }

    [Fact]
    public async Task List_reads_keyvalues_and_ttlKeys_from_state()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);

        var now = DateTime.UtcNow.Ticks;
        var t1 = now + TimeSpan.TicksPerMinute;
        var t2 = now + 2 * TimeSpan.TicksPerMinute;

        var kv = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("data:set:a", new byte[] { 0x01 })
            .Add("data:set:a" + TtlSuffix, BitConverter.GetBytes(t2))
            .Add("data:set:b", new byte[] { 0x02 }) // pas de TTL
            .Add("data:set:c", new byte[] { 0x03 })
            .Add("data:set:c" + TtlSuffix, BitConverter.GetBytes(t1))
            .Add("data:set:__bad__", new byte[] { 0xFF }) // peut être valide selon IdValidator
            .Add("whatever", new byte[] { 0xEE })
            .Add("data:set:orphan" + TtlSuffix, BitConverter.GetBytes(t1)); // ttlKey sans baseKey => ignoré

        var payload = new SlimDataPayload
        {
            KeyValues = kv,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(payload);

        var res = await DataSetRoutes.Handlers.ListAsync(state.Object);

        var ok = Assert.IsType<Ok<List<DataSetEntry>>>(res);
        var list = ok.Value!;

        // ✅ assertions robustes (ne dépendent pas de l'ordre ni d'IdValidator)
        var byId = list.ToDictionary(x => x.Id, x => x.ExpireAtUtcTicks);

        Assert.Equal(t2, byId["a"]);
        Assert.Equal(-1, byId["b"]);
        Assert.Equal(t1, byId["c"]);
    }


    [Fact]
    public async Task Delete_deletes_key()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        db.Setup(d => d.DeleteAsync("data:set:id1"))
          .Returns(Task.CompletedTask);

        var res = await DataSetRoutes.Handlers.DeleteAsync(db.Object, "id1");

        Assert.IsType<NoContent>(res);
        db.VerifyAll();
    }
}
