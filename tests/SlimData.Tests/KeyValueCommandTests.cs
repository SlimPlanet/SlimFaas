using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using DotNext.IO;
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

    private static async Task<KeyValueCommandResult[]> ApplyBatchAsync(
        SlimDataState state,
        params AddKeyValueCommand.BatchItem[] items)
    {
        var results = items.Select(_ => new KeyValueCommandResult()).ToArray();
        var command = new AddKeyValueCommand { Items = items.ToList() };
        var context = new KeyValueCommandBatchContext(results);
        var method = typeof(SlimDataInterpreter).GetMethod(
            "DoAddKeyValueAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var valueTask = (ValueTask)method.Invoke(null, [command, state, context])!;
        await valueTask;
        return results;
    }

    private static AddKeyValueCommand.BatchItem SetItem(string key, string value, long? expireAtUtcTicks = null) =>
        new()
        {
            Operation = KeyValueOperation.Set,
            Key = key,
            Value = Encoding.UTF8.GetBytes(value),
            ExpireAtUtcTicks = expireAtUtcTicks,
            NowTicks = DateTime.UtcNow.Ticks
        };

    private static AddKeyValueCommand.BatchItem IncrementIntegerItem(string key, long delta, long nowTicks = 0) =>
        new()
        {
            Operation = KeyValueOperation.IncrementInteger,
            Key = key,
            IntegerDelta = delta,
            NowTicks = nowTicks > 0 ? nowTicks : DateTime.UtcNow.Ticks
        };

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

    [Fact]
    public async Task Batch_applies_items_in_order_and_returns_one_result_per_item()
    {
        var state = NewState();

        var results = await ApplyBatchAsync(
            state,
            SetItem("counter", "1"),
            IncrementIntegerItem("counter", 1),
            IncrementIntegerItem("counter", 3));

        Assert.All(results, result => Assert.Equal(KeyValueCommandStatus.Applied, result.Status));
        Assert.Equal("1", Encoding.UTF8.GetString(results[0].Value!));
        Assert.Equal("5", Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
        Assert.Equal(2L, results[1].IntegerValue);
        Assert.Equal(5L, results[2].IntegerValue);
    }

    [Fact]
    public async Task Batch_numeric_error_does_not_stop_other_items()
    {
        var state = NewState(ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("bad", Encoding.UTF8.GetBytes("nope")));

        var results = await ApplyBatchAsync(
            state,
            IncrementIntegerItem("bad", 1),
            SetItem("ok", "yes"),
            IncrementIntegerItem("fresh", 2));

        Assert.Equal(KeyValueCommandStatus.InvalidNumber, results[0].Status);
        Assert.Equal(KeyValueCommandStatus.Applied, results[1].Status);
        Assert.Equal(KeyValueCommandStatus.Applied, results[2].Status);
        Assert.Equal("nope", Encoding.UTF8.GetString(state.KeyValues["bad"].Span));
        Assert.Equal("yes", Encoding.UTF8.GetString(state.KeyValues["ok"].Span));
        Assert.Equal("2", Encoding.UTF8.GetString(state.KeyValues["fresh"].Span));
    }

    [Fact]
    public async Task Batch_increment_preserves_ttl_written_by_previous_item()
    {
        var ttl = DateTime.UtcNow.AddMinutes(1).Ticks;
        var state = NewState();

        var results = await ApplyBatchAsync(
            state,
            SetItem("counter", "1", ttl),
            IncrementIntegerItem("counter", 1));

        Assert.All(results, result => Assert.Equal(KeyValueCommandStatus.Applied, result.Status));
        Assert.Equal(2L, results[1].IntegerValue);
        Assert.Equal("2", Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
        Assert.Equal(ttl, BitConverter.ToInt64(state.KeyValues[SlimDataInterpreter.TtlKey("counter")].Span));
    }

    [Fact]
    public async Task AddKeyValueCommand_roundtrips_batch_items()
    {
        var command = new AddKeyValueCommand
        {
            Items =
            [
                SetItem("counter", "1"),
                IncrementIntegerItem("counter", 1),
                IncrementIntegerItem("counter", 3)
            ]
        };

        var bytes = await DataTransferObject.ToByteArrayAsync(command, null, CancellationToken.None);
        await using var stream = new MemoryStream(bytes);
        var reader = IAsyncBinaryReader.Create(stream, new byte[256]);
        var roundtripped = await AddKeyValueCommand.ReadFromAsync(reader, CancellationToken.None);
        var items = roundtripped.EffectiveItems();

        Assert.Equal(3, items.Count);
        Assert.Equal(KeyValueOperation.Set, items[0].Operation);
        Assert.Equal(KeyValueOperation.IncrementInteger, items[1].Operation);
        Assert.Equal(1L, items[1].IntegerDelta);
        Assert.Equal(KeyValueOperation.IncrementInteger, items[2].Operation);
        Assert.Equal(3L, items[2].IntegerDelta);
    }

    [Fact]
    public async Task AddKeyValueCommand_serializes_a_contiguous_current_envelope_with_exact_length()
    {
        var command = new AddKeyValueCommand
        {
            Items =
            [
                SetItem("history-é", new string('x', 128)),
                IncrementIntegerItem("history-counter", 1)
            ]
        };

        var payload = command.Serialize();

        Assert.Equal([0x53, 0x4C, 0x44, 0x43, 0x01], payload[..5]);
        Assert.Equal(3, payload[5]);
        Assert.Equal(((IDataTransferObject)command).Length, payload.LongLength);

        await using var stream = new MemoryStream(payload, writable: false);
        var reader = IAsyncBinaryReader.Create(stream, new byte[32]);
        var roundtripped = await AddKeyValueCommand.ReadFromAsync(reader, CancellationToken.None);
        Assert.Equal(2, roundtripped.EffectiveItems().Count);
    }

}
