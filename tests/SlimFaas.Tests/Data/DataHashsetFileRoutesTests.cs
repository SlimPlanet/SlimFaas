using System.Collections.Immutable;
using System.Text;
using DotNext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas;

public sealed class DataHashsetFileRoutesTests
{
    private const string TtlSuffix = "${slimfaas-timetolive}$";
    private const string TtlField = "__ttl__";

    [Fact]
    public async Task Post_hashset_sets_value_and_returns_id()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var body = Encoding.UTF8.GetBytes("hello");
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(body);

        db.Setup(d => d.HashSetAsync(
                "data:hashset:element1",
                It.Is<IDictionary<string, byte[]>>(m => m.ContainsKey("value") && m["value"].SequenceEqual(body)),
                123L))
          .Returns(Task.CompletedTask);

        var res = await DataHashsetFileRoutes.Handlers.PostAsync(ctx, db.Object, "element1", 123L, CancellationToken.None);

        var ok = Assert.IsType<Ok<string>>(res);
        Assert.Equal("element1", ok.Value);

        db.VerifyAll();
    }

    [Fact]
    public async Task Get_returns_bytes_when_found()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var payload = new byte[] { 1, 2, 3 };

        db.Setup(d => d.HashGetAllAsync("data:hashset:id1"))
          .ReturnsAsync(new Dictionary<string, byte[]>
          {
              ["value"] = payload
          });

        var res = await DataHashsetFileRoutes.Handlers.GetAsync(db.Object, "id1");

        var file = Assert.IsType<FileContentHttpResult>(res);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal(payload, file.FileContents);

        db.VerifyAll();
    }

    [Fact]
    public async Task List_reads_hashsets_and_ttl_meta_hashsets_from_state()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);

        var now = DateTime.UtcNow.Ticks;
        var t1 = now + TimeSpan.TicksPerMinute;
        var t2 = now + 2 * TimeSpan.TicksPerMinute;

        var hs = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
            // main hashsets
            .Add("data:hashset:a", ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add("value", new byte[] { 0x01 }))
            .Add("data:hashset:b", ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add("value", new byte[] { 0x02 }))
            .Add("data:hashset:c", ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add("value", new byte[] { 0x03 }))
            // ttl meta hashsets
            .Add("data:hashset:a" + TtlSuffix, ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add(TtlField, BitConverter.GetBytes(t2)))
            .Add("data:hashset:c" + TtlSuffix, ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add(TtlField, BitConverter.GetBytes(t1)))
            // ttlKey orphelin (sans base)
            .Add("data:hashset:orphan" + TtlSuffix, ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add(TtlField, BitConverter.GetBytes(t1)));

        var payload = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Hashsets = hs,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(payload);

        var res = await DataHashsetFileRoutes.Handlers.ListAsync(state.Object);

        var ok = Assert.IsType<Ok<List<DataHashsetFileRoutes.DataHashsetEntry>>>(res);
        var list = ok.Value!;

        // b (null TTL) doit être avant c (t1) avant a (t2)
        Assert.Equal(3, list.Count);

        Assert.Equal("b", list[0].Id);
        Assert.Null(list[0].ExpireAtUtcTicks);

        Assert.Equal("c", list[1].Id);
        Assert.Equal(t1, list[1].ExpireAtUtcTicks);

        Assert.Equal("a", list[2].Id);
        Assert.Equal(t2, list[2].ExpireAtUtcTicks);
    }

    [Fact]
    public async Task Delete_deletes_whole_hashset()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        db.Setup(d => d.HashSetDeleteAsync("data:hashset:id1", ""))
          .Returns(Task.CompletedTask);

        var res = await DataHashsetFileRoutes.Handlers.DeleteAsync(db.Object, "id1");

        Assert.IsType<NoContent>(res);
        db.VerifyAll();
    }
}
