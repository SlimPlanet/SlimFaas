using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class SlimDataCommandCodecTests
{
    private delegate ValueTask ReadCommand(IAsyncBinaryReader reader);

    [Fact]
    public async Task All_commands_roundtrip_and_report_their_exact_serialized_length()
    {
        var bytes127 = Enumerable.Repeat((byte)0x7F, 127).ToArray();
        var bytes128 = Enumerable.Repeat((byte)0x80, 128).ToArray();

        var addHash = new AddHashSetCommand
        {
            Key = "hash-é-雪",
            ExpireAtUtcTicks = 42,
            Value = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                ["empty"] = ReadOnlyMemory<byte>.Empty,
                ["value-127"] = bytes127,
                ["value-128"] = bytes128
            }
        };
        var addHashResult = await RoundtripAsync(addHash, AddHashSetCommand.ReadFromAsync);
        Assert.Equal(addHash.Key, addHashResult.Key);
        Assert.Equal(bytes127, addHashResult.Value["value-127"].ToArray());
        Assert.Equal(bytes128, addHashResult.Value["value-128"].ToArray());

        var addKeyValue = new AddKeyValueCommand
        {
            Items =
            [
                new AddKeyValueCommand.BatchItem
                {
                    Operation = KeyValueOperation.Set,
                    Key = "kv-é-雪",
                    Value = bytes127,
                    NowTicks = 12
                },
                new AddKeyValueCommand.BatchItem
                {
                    Operation = KeyValueOperation.Set,
                    Key = "empty",
                    Value = ReadOnlyMemory<byte>.Empty,
                    NowTicks = 13
                },
                new AddKeyValueCommand.BatchItem
                {
                    Operation = KeyValueOperation.Set,
                    Key = "value-128",
                    Value = bytes128,
                    NowTicks = 14
                }
            ]
        };
        var addKeyValueResult = await RoundtripAsync(addKeyValue, AddKeyValueCommand.ReadFromAsync);
        Assert.Equal(bytes127, addKeyValueResult.EffectiveItems()[0].Value.ToArray());
        Assert.Empty(addKeyValueResult.EffectiveItems()[1].Value.ToArray());
        Assert.Equal(bytes128, addKeyValueResult.EffectiveItems()[2].Value.ToArray());

        var deleteKey = new DeleteKeyValueCommand { Key = "delete-é-雪" };
        Assert.Equal(deleteKey.Key, (await RoundtripAsync(deleteKey, DeleteKeyValueCommand.ReadFromAsync)).Key);

        var deleteHash = new DeleteHashSetCommand { Key = "hash-雪", DictionaryKey = "field-é" };
        var deleteHashResult = await RoundtripAsync(deleteHash, DeleteHashSetCommand.ReadFromAsync);
        Assert.Equal(deleteHash.Key, deleteHashResult.Key);
        Assert.Equal(deleteHash.DictionaryKey, deleteHashResult.DictionaryKey);

        var push = new ListLeftPushBatchCommand
        {
            Items =
            [
                new ListLeftPushBatchCommand.BatchItem
                {
                    Key = "queue-雪",
                    Identifier = "id-é",
                    NowTicks = 15,
                    RetryTimeout = 30,
                    Retries = [1, 2],
                    HttpStatusCodesWorthRetrying = [429, 500],
                    Value = bytes128
                }
            ]
        };
        var pushResult = await RoundtripAsync(push, ListLeftPushBatchCommand.ReadFromAsync);
        Assert.Equal(bytes128, Assert.Single(pushResult.Items).Value.ToArray());

        var callback = new ListCallbackCommand
        {
            Key = "queue-é",
            NowTicks = 16,
            CallbackElements = [new CallbackElement("id-雪", 204)]
        };
        var callbackResult = await RoundtripAsync(callback, ListCallbackCommand.ReadFromAsync);
        Assert.Equal("id-雪", Assert.Single(callbackResult.CallbackElements).Identifier);

        var callbackBatch = new ListCallbackBatchCommand
        {
            Items =
            [
                new ListCallbackBatchCommand.BatchItem
                {
                    Key = "queue-雪",
                    NowTicks = 17,
                    CallbackElements = [new CallbackElement("id-é", 200)]
                }
            ]
        };
        var callbackBatchResult = await RoundtripAsync(callbackBatch, ListCallbackBatchCommand.ReadFromAsync);
        Assert.Equal("id-é", Assert.Single(Assert.Single(callbackBatchResult.Items).CallbackElements).Identifier);

        var pop = new ListRightPopCommand
        {
            Key = "queue-雪",
            Count = 3,
            NowTicks = 18,
            IdTransaction = "tx-é",
            ReservedIps = ["127.0.0.1", "hôte-雪"]
        };
        var popResult = await RoundtripAsync(pop, ListRightPopCommand.ReadFromAsync);
        Assert.Equal(pop.IdTransaction, popResult.IdTransaction);
        Assert.Equal(pop.ReservedIps, popResult.ReservedIps);
    }

    [Fact]
    public async Task Every_command_rejects_bad_magic_version_oversized_prefix_and_truncation()
    {
        var cases = await CreateCommandCasesAsync();

        foreach (var (name, validPayload, read) in cases)
        {
            var badMagic = validPayload.ToArray();
            badMagic[0] ^= 0xFF;
            await AssertInvalidAsync(name, badMagic, read);

            var badVersion = validPayload.ToArray();
            badVersion[4] = 0x7F;
            await AssertInvalidAsync(name, badVersion, read);

            await AssertInvalidAsync(name, validPayload[..^1], read);

            var invalidPrefix = validPayload[..5]
                .Concat(BitConverter.GetBytes(int.MaxValue))
                .ToArray();
            if (name == nameof(AddKeyValueCommand))
            {
                invalidPrefix = validPayload[..5]
                    .Concat([(byte)3, (byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0x07])
                    .ToArray();
            }

            await AssertInvalidAsync(name, invalidPrefix, read);
        }
    }

    [Fact]
    public async Task Negative_and_excessive_collection_counts_are_rejected_before_allocation()
    {
        var negativeBatch = CurrentHeader().Concat(BitConverter.GetBytes(-1)).ToArray();
        await AssertInvalidAsync(
            nameof(ListLeftPushBatchCommand),
            negativeBatch,
            async reader => { _ = await ListLeftPushBatchCommand.ReadFromAsync(reader, CancellationToken.None); });
        await AssertInvalidAsync(
            nameof(ListCallbackBatchCommand),
            negativeBatch,
            async reader => { _ = await ListCallbackBatchCommand.ReadFromAsync(reader, CancellationToken.None); });

        var excessiveBatch = CurrentHeader().Concat(BitConverter.GetBytes(1025)).ToArray();
        await AssertInvalidAsync(
            nameof(ListLeftPushBatchCommand),
            excessiveBatch,
            async reader => { _ = await ListLeftPushBatchCommand.ReadFromAsync(reader, CancellationToken.None); });
        await AssertInvalidAsync(
            nameof(ListCallbackBatchCommand),
            excessiveBatch,
            async reader => { _ = await ListCallbackBatchCommand.ReadFromAsync(reader, CancellationToken.None); });

        var command = new AddKeyValueCommand
        {
            Operation = KeyValueOperation.Set,
            Key = "key",
            Value = new byte[] { 1 },
            NowTicks = 1
        };
        var payload = await SerializeAndAssertLengthAsync(command);
        var innerOffset = 6 + GetCompressedPrefixLength(payload.AsSpan(6));
        BitConverter.GetBytes(-1).CopyTo(payload, innerOffset);
        await AssertInvalidAsync(
            nameof(AddKeyValueCommand),
            payload,
            async reader => { _ = await AddKeyValueCommand.ReadFromAsync(reader, CancellationToken.None); });

        var nonCanonicalCompressedLength = CurrentHeader()
            .Concat([(byte)3, (byte)0x80, (byte)0x00])
            .ToArray();
        await AssertInvalidAsync(
            nameof(AddKeyValueCommand),
            nonCanonicalCompressedLength,
            async reader => { _ = await AddKeyValueCommand.ReadFromAsync(reader, CancellationToken.None); });

        var overflowingCompressedLength = CurrentHeader()
            .Concat([(byte)3, (byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0xFF])
            .ToArray();
        await AssertInvalidAsync(
            nameof(AddKeyValueCommand),
            overflowingCompressedLength,
            async reader => { _ = await AddKeyValueCommand.ReadFromAsync(reader, CancellationToken.None); });
    }

    private static async Task<T> RoundtripAsync<T>(
        T command,
        Func<IAsyncBinaryReader, CancellationToken, ValueTask<T>> read)
        where T : struct, ICommand<T>
    {
        var bytes = await SerializeAndAssertLengthAsync(command);
        await using var stream = new MemoryStream(bytes, writable: false);
        var reader = IAsyncBinaryReader.Create(stream, new byte[256]);
        return await read(reader, CancellationToken.None);
    }

    private static async Task<byte[]> SerializeAndAssertLengthAsync<T>(T command)
        where T : struct, ICommand<T>
    {
        var bytes = await DataTransferObject.ToByteArrayAsync(command, null, CancellationToken.None);
        Assert.Equal(((IDataTransferObject)command).Length, bytes.LongLength);
        return bytes;
    }

    private static async Task<IReadOnlyList<(string Name, byte[] Payload, ReadCommand Read)>>
        CreateCommandCasesAsync()
    {
        return
        [
            (nameof(AddHashSetCommand), await SerializeAndAssertLengthAsync(new AddHashSetCommand
            {
                Key = "hash",
                Value = new Dictionary<string, ReadOnlyMemory<byte>> { ["field"] = new byte[] { 1 } }
            }), async reader => { _ = await AddHashSetCommand.ReadFromAsync(reader, CancellationToken.None); }),
            (nameof(AddKeyValueCommand), await SerializeAndAssertLengthAsync(new AddKeyValueCommand
            {
                Operation = KeyValueOperation.Set,
                Key = "key",
                Value = new byte[] { 1 },
                NowTicks = 1
            }), async reader => { _ = await AddKeyValueCommand.ReadFromAsync(reader, CancellationToken.None); }),
            (nameof(DeleteKeyValueCommand), await SerializeAndAssertLengthAsync(new DeleteKeyValueCommand
            {
                Key = "key"
            }), async reader => { _ = await DeleteKeyValueCommand.ReadFromAsync(reader, CancellationToken.None); }),
            (nameof(DeleteHashSetCommand), await SerializeAndAssertLengthAsync(new DeleteHashSetCommand
            {
                Key = "hash",
                DictionaryKey = "field"
            }), async reader => { _ = await DeleteHashSetCommand.ReadFromAsync(reader, CancellationToken.None); }),
            (nameof(ListLeftPushBatchCommand), await SerializeAndAssertLengthAsync(new ListLeftPushBatchCommand
            {
                Items = []
            }), async reader => { _ = await ListLeftPushBatchCommand.ReadFromAsync(reader, CancellationToken.None); }),
            (nameof(ListCallbackCommand), await SerializeAndAssertLengthAsync(new ListCallbackCommand
            {
                Key = "queue",
                CallbackElements = []
            }), async reader => { _ = await ListCallbackCommand.ReadFromAsync(reader, CancellationToken.None); }),
            (nameof(ListCallbackBatchCommand), await SerializeAndAssertLengthAsync(new ListCallbackBatchCommand
            {
                Items = []
            }), async reader => { _ = await ListCallbackBatchCommand.ReadFromAsync(reader, CancellationToken.None); }),
            (nameof(ListRightPopCommand), await SerializeAndAssertLengthAsync(new ListRightPopCommand
            {
                Key = "queue",
                IdTransaction = "tx",
                ReservedIps = []
            }), async reader => { _ = await ListRightPopCommand.ReadFromAsync(reader, CancellationToken.None); })
        ];
    }

    private static async Task AssertInvalidAsync(string commandName, byte[] payload, ReadCommand read)
    {
        await using var stream = new MemoryStream(payload, writable: false);
        var reader = IAsyncBinaryReader.Create(stream, new byte[64]);
        var exception = await Assert.ThrowsAnyAsync<IOException>(async () => await read(reader));
        Assert.DoesNotContain("OutOfMemoryException", exception.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(commandName);
    }

    private static byte[] CurrentHeader()
        => [0x53, 0x4C, 0x44, 0x43, 0x01];

    private static int GetCompressedPrefixLength(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if ((bytes[i] & 0x80) == 0)
                return i + 1;
        }

        throw new InvalidDataException("Missing compressed length prefix terminator.");
    }
}
