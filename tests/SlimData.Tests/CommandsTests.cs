using DotNext.Net.Cluster.Consensus.Raft.Commands;
using MemoryPack;
using SlimData.Commands;

namespace SlimData.Tests;

public class CommandsTests
{
    private protected static byte[] RandomBytes(int size)
    {
        var result = new byte[size];
        Random.Shared.NextBytes(result);
        return result;
    }

    /*[Fact]
    public static async Task InterpreterWithPersistentState()
    {
        byte[] bytes = RandomBytes(1000);
        var wal = new SlimPersistentState(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var entry1 = new LogEntry<ListLeftPushCommand>()
        {
            Term = wal,
            Command = new()
            {
                Key = "youhou",
                Value = bytes,
                Identifier = "1",
                RetryTimeout = 100,
                Retries = new List<int> { 1, 2, 3 },
                NowTicks = DateTime.UtcNow.Ticks,
                HttpStatusCodesWorthRetrying = new List<int>()
            }
        };
        await wal.AppendAsync(entry1);
        Assert.Empty(wal.SlimDataState.Queues);
        await wal.CommitAsync(CancellationToken.None);
        Assert.Equal(bytes, wal.SlimDataState.Queues["youhou"].First().Value.ToArray());

        var bin = MemoryPackSerializer.Serialize(3);
        var final = MemoryPackSerializer.Deserialize<int>(bin);
        Assert.Equal(3, final);
    }*/


}
