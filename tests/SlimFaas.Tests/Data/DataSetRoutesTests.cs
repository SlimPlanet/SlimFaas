using System.Collections.Immutable;
using System.Net;
using System.Text;
using DotNext;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using Xunit;

public sealed class DataSetRoutesTests
{
    private const string TtlSuffix = "${slimfaas-timetolive}$";

    private static KeyValueCommandResult Applied(byte[]? value = null)
    {
        var result = new KeyValueCommandResult();
        result.SetApplied(value ?? Array.Empty<byte>());
        return result;
    }

    private static KeyValueCommandResult AppliedInteger(long value)
    {
        var result = new KeyValueCommandResult();
        result.SetApplied(Encoding.UTF8.GetBytes(value.ToString()), integerValue: value);
        return result;
    }

    private static KeyValueCommandResult AppliedDecimal(decimal value)
    {
        var result = new KeyValueCommandResult();
        result.SetApplied(Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)), decimalValue: value);
        return result;
    }

    private static KeyValueCommandResult InvalidNumber()
    {
        var result = new KeyValueCommandResult();
        result.SetError(KeyValueCommandStatus.InvalidNumber, "Value is not numeric.");
        return result;
    }

    private static async Task<IHost> BuildHostAsync(IDatabaseService db)
    {
        var state = new Mock<ISupplier<SlimDataPayload>>();
        var accessPolicy = new Mock<IFunctionAccessPolicy>();

        return await new HostBuilder()
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(db);
                        services.AddSingleton(state.Object);
                        services.AddSingleton(accessPolicy.Object);
                        services.Configure<DataOptions>(options =>
                            options.DefaultVisibility = FunctionVisibility.Public);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapDataSetRoutes());
                    });
            })
            .StartAsync();
    }

    [Fact]
    public async Task Post_sets_value_and_returns_id()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var body = Encoding.UTF8.GetBytes("hello");
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(body);

        db.Setup(d => d.SetAsync("data:set:element1", It.Is<byte[]>(b => b.SequenceEqual(body)), 123L))
          .ReturnsAsync(Applied(body));

        var res = await DataSetRoutes.Handlers.PostAsync(ctx, db.Object, "element1", 123L, CancellationToken.None);

        var ok = Assert.IsType<Ok<string>>(res);
        Assert.Equal("element1", ok.Value);

        db.VerifyAll();
    }

    [Fact]
    public async Task Post_route_uses_path_id_and_forwards_query_ttl()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var body = Encoding.UTF8.GetBytes("hello");
        db.Setup(d => d.SetAsync(
                "data:set:youhou",
                It.Is<byte[]>(value => value.SequenceEqual(body)),
                20_000L))
            .ReturnsAsync(Applied(body));

        using var host = await BuildHostAsync(db.Object);
        using var content = new ByteArrayContent(body);
        using var response = await host.GetTestClient()
            .PostAsync("/data/sets/youhou?ttl=20000", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        db.VerifyAll();
    }

    [Fact]
    public async Task Incr_route_forwards_query_ttl_with_empty_body()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:david",
                (byte[]?)null,
                20_000L,
                KeyValueOperation.IncrementInteger,
                1,
                0m))
            .ReturnsAsync(AppliedInteger(1));

        using var host = await BuildHostAsync(db.Object);
        using var content = new ByteArrayContent([]);
        using var response = await host.GetTestClient()
            .PostAsync("/data/sets/david/incr?ttl=20000", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", await response.Content.ReadAsStringAsync());
        db.VerifyAll();
    }

    [Fact]
    public async Task Incr_calls_set_with_integer_increment_and_returns_new_value()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                null,
                KeyValueOperation.IncrementInteger,
                1,
                0m))
          .ReturnsAsync(AppliedInteger(3));

        var res = await DataSetRoutes.Handlers.IncrAsync(db.Object, "counter");

        var text = Assert.IsType<ContentHttpResult>(res);
        Assert.Equal("3", text.ResponseContent);
        db.VerifyAll();
    }

    [Fact]
    public async Task DecrBy_negates_delta()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                null,
                KeyValueOperation.IncrementInteger,
                -5,
                0m))
          .ReturnsAsync(AppliedInteger(7));

        var res = await DataSetRoutes.Handlers.DecrByAsync(db.Object, "counter", 5);

        var text = Assert.IsType<ContentHttpResult>(res);
        Assert.Equal("7", text.ResponseContent);
        db.VerifyAll();
    }

    [Fact]
    public async Task IncrByFloat_calls_set_with_decimal_increment_and_returns_new_value()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                null,
                KeyValueOperation.IncrementFloat,
                0,
                1.5m))
          .ReturnsAsync(AppliedDecimal(2.5m));

        var res = await DataSetRoutes.Handlers.IncrByFloatAsync(db.Object, "counter", 1.5m);

        var text = Assert.IsType<ContentHttpResult>(res);
        Assert.Equal("2.5", text.ResponseContent);
        db.VerifyAll();
    }

    [Fact]
    public async Task Incr_returns_conflict_when_command_rejects_value()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                null,
                KeyValueOperation.IncrementInteger,
                1,
                0m))
          .ReturnsAsync(InvalidNumber());

        var res = await DataSetRoutes.Handlers.IncrAsync(db.Object, "counter");

        var problem = Assert.IsType<ProblemHttpResult>(res);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        db.VerifyAll();
    }

    [Theory]
    [InlineData(0, StatusCodes.Status429TooManyRequests)]
    [InlineData(1, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(2, StatusCodes.Status503ServiceUnavailable)]
    public async Task Incr_maps_capacity_and_quorum_failures(int failure, int expectedStatus)
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        Exception exception = failure switch
        {
            0 => new BatchQueueFullException("kv"),
            1 => new BatchItemTooLargeException("kv", 5, 4),
            _ => new SlimDataUnavailableException("No active quorum.")
        };
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                null,
                KeyValueOperation.IncrementInteger,
                1,
                0m))
          .ThrowsAsync(exception);

        var result = await DataSetRoutes.Handlers.IncrAsync(db.Object, "counter");

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(expectedStatus, problem.StatusCode);
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
        var expired = now - TimeSpan.TicksPerMinute;

        var kv = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("data:set:a", new byte[] { 0x01 })
            .Add("data:set:a" + SlimDataInterpreter.TimeToLivePostfix, BitConverter.GetBytes(t2))
            .Add("data:set:b", new byte[] { 0x02 }) // pas de TTL
            .Add("data:set:c", new byte[] { 0x03 })
            .Add("data:set:c" + SlimDataInterpreter.TimeToLivePostfix, BitConverter.GetBytes(t1))
            .Add("data:set:expired", new byte[] { 0x04 })
            .Add("data:set:expired" + SlimDataInterpreter.TimeToLivePostfix, BitConverter.GetBytes(expired))
            .Add("data:set:__bad__", new byte[] { 0xFF }) // peut être valide selon IdValidator
            .Add("whatever", new byte[] { 0xEE })
            .Add("data:set:orphan" + SlimDataInterpreter.TimeToLivePostfix, BitConverter.GetBytes(t1)); // ttlKey sans baseKey => ignoré

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
        Assert.False(byId.ContainsKey("expired"));
    }


    [Fact]
    public async Task Incr_forwards_ttl_to_database()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                60_000L,
                KeyValueOperation.IncrementInteger,
                1,
                0m))
          .ReturnsAsync(AppliedInteger(1));

        var res = await DataSetRoutes.Handlers.IncrAsync(db.Object, "counter", 60_000L);

        var text = Assert.IsType<ContentHttpResult>(res);
        Assert.Equal("1", text.ResponseContent);
        db.VerifyAll();
    }

    [Fact]
    public async Task IncrBy_forwards_ttl_to_database()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                30_000L,
                KeyValueOperation.IncrementInteger,
                5,
                0m))
          .ReturnsAsync(AppliedInteger(5));

        var res = await DataSetRoutes.Handlers.IncrByAsync(db.Object, "counter", 5, 30_000L);

        var text = Assert.IsType<ContentHttpResult>(res);
        Assert.Equal("5", text.ResponseContent);
        db.VerifyAll();
    }

    [Fact]
    public async Task Decr_forwards_ttl_to_database()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                15_000L,
                KeyValueOperation.IncrementInteger,
                -1,
                0m))
          .ReturnsAsync(AppliedInteger(-1));

        var res = await DataSetRoutes.Handlers.DecrAsync(db.Object, "counter", 15_000L);

        var text = Assert.IsType<ContentHttpResult>(res);
        Assert.Equal("-1", text.ResponseContent);
        db.VerifyAll();
    }

    [Fact]
    public async Task DecrBy_forwards_ttl_to_database()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                45_000L,
                KeyValueOperation.IncrementInteger,
                -3,
                0m))
          .ReturnsAsync(AppliedInteger(-3));

        var res = await DataSetRoutes.Handlers.DecrByAsync(db.Object, "counter", 3, 45_000L);

        var text = Assert.IsType<ContentHttpResult>(res);
        Assert.Equal("-3", text.ResponseContent);
        db.VerifyAll();
    }

    [Fact]
    public async Task IncrByFloat_forwards_ttl_to_database()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        db.Setup(d => d.SetAsync(
                "data:set:counter",
                (byte[]?)null,
                90_000L,
                KeyValueOperation.IncrementFloat,
                0,
                1.5m))
          .ReturnsAsync(AppliedDecimal(1.5m));

        var res = await DataSetRoutes.Handlers.IncrByFloatAsync(db.Object, "counter", 1.5m, 90_000L);

        var text = Assert.IsType<ContentHttpResult>(res);
        Assert.Equal("1.5", text.ResponseContent);
        db.VerifyAll();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Incr_rejects_invalid_ttl(long ttl)
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var res = await DataSetRoutes.Handlers.IncrAsync(db.Object, "counter", ttl);

        Assert.IsType<BadRequest<string>>(res);
        db.VerifyAll(); // no call expected
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-10L)]
    public async Task IncrBy_rejects_invalid_ttl(long ttl)
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var res = await DataSetRoutes.Handlers.IncrByAsync(db.Object, "counter", 1, ttl);

        Assert.IsType<BadRequest<string>>(res);
    }

    [Fact]
    public async Task Decr_rejects_invalid_ttl()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var res = await DataSetRoutes.Handlers.DecrAsync(db.Object, "counter", -1L);

        Assert.IsType<BadRequest<string>>(res);
    }

    [Fact]
    public async Task DecrBy_rejects_invalid_ttl()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var res = await DataSetRoutes.Handlers.DecrByAsync(db.Object, "counter", 1, 0L);

        Assert.IsType<BadRequest<string>>(res);
    }

    [Fact]
    public async Task IncrByFloat_rejects_invalid_ttl()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var res = await DataSetRoutes.Handlers.IncrByFloatAsync(db.Object, "counter", 1m, -5L);

        Assert.IsType<BadRequest<string>>(res);
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
