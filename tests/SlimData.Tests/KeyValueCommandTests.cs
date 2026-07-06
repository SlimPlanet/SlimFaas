using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class KeyValueCommandTests
{
    private static SlimDataState NewState(ImmutableDictionary<string, ReadOnlyMemory<byte>>? keyValues = null) =>
        new(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            keyValues ?? ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);

    private static async Task<KeyValueCommandResult> ApplyAsync(SlimDataState state, AddKeyValueCommand command)
    {
        var result = new KeyValueCommandResult();
        var interpreter = SlimDataInterpreter.InitInterpreter(state);
        var entry = new LogEntry<AddKeyValueCommand>
        {
            Term = 1,
            Command = command,
            Context = result
        };

        await interpreter.InterpretAsync(entry, result, CancellationToken.None);
        return result;
    }

    private static AddKeyValueCommand IncrementInteger(string key, long delta, long nowTicks = 0) =>
        new()
        {
            Operation = KeyValueOperation.IncrementInteger,
            Key = key,
            IntegerDelta = delta,
            NowTicks = nowTicks > 0 ? nowTicks : DateTime.UtcNow.Ticks
        };

    private static AddKeyValueCommand IncrementFloat(string key, decimal delta, long nowTicks = 0) =>
        new()
        {
            Operation = KeyValueOperation.IncrementFloat,
            Key = key,
            FloatDelta = delta,
            NowTicks = nowTicks > 0 ? nowTicks : DateTime.UtcNow.Ticks
        };

    [Fact]
    public async Task Set_replaces_value_and_removes_existing_ttl_when_no_ttl_is_provided()
    {
        var state = NewState(ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("k", Encoding.UTF8.GetBytes("old"))
            .Add(SlimDataInterpreter.TtlKey("k"), BitConverter.GetBytes(DateTime.UtcNow.AddMinutes(1).Ticks)));

        var result = await ApplyAsync(state, new AddKeyValueCommand
        {
            Operation = KeyValueOperation.Set,
            Key = "k",
            Value = Encoding.UTF8.GetBytes("new")
        });

        Assert.Equal(KeyValueCommandStatus.Applied, result.Status);
        Assert.Equal("new", Encoding.UTF8.GetString(state.KeyValues["k"].Span));
        Assert.False(state.KeyValues.ContainsKey(SlimDataInterpreter.TtlKey("k")));
    }

    [Fact]
    public async Task Increment_integer_starts_from_zero_when_key_is_missing()
    {
        var state = NewState();

        var result = await ApplyAsync(state, IncrementInteger("counter", 1));

        Assert.Equal(KeyValueCommandStatus.Applied, result.Status);
        Assert.Equal(1L, result.IntegerValue);
        Assert.Equal("1", Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
    }

    [Fact]
    public async Task Increment_integer_preserves_existing_ttl()
    {
        var ttl = DateTime.UtcNow.AddMinutes(1).Ticks;
        var state = NewState(ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("counter", Encoding.UTF8.GetBytes("41"))
            .Add(SlimDataInterpreter.TtlKey("counter"), BitConverter.GetBytes(ttl)));

        var result = await ApplyAsync(state, IncrementInteger("counter", 1));

        Assert.Equal(KeyValueCommandStatus.Applied, result.Status);
        Assert.Equal(42L, result.IntegerValue);
        Assert.Equal("42", Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
        Assert.Equal(ttl, BitConverter.ToInt64(state.KeyValues[SlimDataInterpreter.TtlKey("counter")].Span));
    }

    [Fact]
    public async Task Increment_integer_treats_expired_key_as_missing_and_removes_old_ttl()
    {
        var now = DateTime.UtcNow.Ticks;
        var state = NewState(ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("counter", Encoding.UTF8.GetBytes("99"))
            .Add(SlimDataInterpreter.TtlKey("counter"), BitConverter.GetBytes(now - TimeSpan.TicksPerSecond)));

        var result = await ApplyAsync(state, IncrementInteger("counter", 1, now));

        Assert.Equal(KeyValueCommandStatus.Applied, result.Status);
        Assert.Equal(1L, result.IntegerValue);
        Assert.Equal("1", Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
        Assert.False(state.KeyValues.ContainsKey(SlimDataInterpreter.TtlKey("counter")));
    }

    [Fact]
    public async Task Increment_integer_rejects_non_integer_without_mutation()
    {
        var state = NewState(ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("counter", Encoding.UTF8.GetBytes("nope")));

        var result = await ApplyAsync(state, IncrementInteger("counter", 1));

        Assert.Equal(KeyValueCommandStatus.InvalidNumber, result.Status);
        Assert.Equal("nope", Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
    }

    [Fact]
    public async Task Increment_integer_rejects_overflow_without_mutation()
    {
        var state = NewState(ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("counter", Encoding.UTF8.GetBytes(long.MaxValue.ToString(CultureInfo.InvariantCulture))));

        var result = await ApplyAsync(state, IncrementInteger("counter", 1));

        Assert.Equal(KeyValueCommandStatus.Overflow, result.Status);
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
    }

    [Fact]
    public async Task Increment_float_writes_canonical_decimal_value()
    {
        var state = NewState(ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("counter", Encoding.UTF8.GetBytes("1.25")));

        var result = await ApplyAsync(state, IncrementFloat("counter", 0.75m));

        Assert.Equal(KeyValueCommandStatus.Applied, result.Status);
        Assert.Equal(2m, result.DecimalValue);
        Assert.Equal("2", Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
    }
}
