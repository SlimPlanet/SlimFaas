using System.Collections.Immutable;
using DotNext;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimData.Expiration;
using SlimFaas;
using Xunit;

public sealed class SlimDataExpirationCleanerTests
{
/*
    private static async IAsyncEnumerable<FileMetadataEntry> EmptyMeta([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }

    private static async IAsyncEnumerable<FileMetadataEntry> Meta(params FileMetadataEntry[] entries)
    {
        foreach (var e in entries)
            yield return e;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CleanupOnceAsync_deletes_expired_keyvalue_and_deletes_local_file_for_file_meta()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        var metaKey = "data:file:abc:meta";
        var ttlKey = metaKey + SlimDataInterpreter.TimeToLivePostfix;

        var nowTicks = DateTime.UtcNow.Ticks;
        var expiredTicks = nowTicks - TimeSpan.TicksPerSecond;

        var data = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                .Add(metaKey, new byte[] { 0x01, 0x02 })
                .Add(ttlKey, BitConverter.GetBytes(expiredTicks))
                // non-expired
                .Add("k1", new byte[] { 0xAA })
                .Add("k1" + SlimDataInterpreter.TimeToLivePostfix, BitConverter.GetBytes(nowTicks + TimeSpan.TicksPerHour)),
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(data);

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
           .Returns(EmptyMeta());

        // metaKey expirée => delete local file "abc"
        files.Setup(f => f.DeleteAsync("abc", It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        // suppression physique via RAFT
        db.Setup(d => d.DeleteAsync(metaKey))
          .Returns(Task.CompletedTask);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);

        await sut.CleanupOnceAsync(CancellationToken.None);

        files.VerifyAll();
        db.VerifyAll();

        // non-expired ne doit pas être supprimé
        db.Verify(d => d.DeleteAsync("k1"), Times.Never);
    }

    [Fact]
    public async Task CleanupOnceAsync_deletes_expired_hashset_and_its_ttl_hashset()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        var nowTicks = DateTime.UtcNow.Ticks;
        var expiredTicks = nowTicks - TimeSpan.TicksPerSecond;

        var baseKey = "hs:myset";
        var ttlKey = baseKey + SlimDataInterpreter.TimeToLivePostfix;

        var ttlDict = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add(SlimDataInterpreter.HashsetTtlField, BitConverter.GetBytes(expiredTicks));

        var data = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
                .Add(ttlKey, ttlDict),
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(data);
        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
            .Returns(EmptyMeta());

        db.Setup(d => d.HashSetDeleteAsync(baseKey, ""))  // si tu appelles sans param, remplace par Setup(d => d.HashSetDeleteAsync(baseKey))
          .Returns(Task.CompletedTask);

        db.Setup(d => d.HashSetDeleteAsync(ttlKey, ""))
          .Returns(Task.CompletedTask);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);

        await sut.CleanupOnceAsync(CancellationToken.None);

        db.VerifyAll();
        files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CleanupOnceAsync_does_nothing_when_not_expired()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        var nowTicks = DateTime.UtcNow.Ticks;
        var futureTicks = nowTicks + TimeSpan.TicksPerHour;

        var k = "x";

        var data = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                .Add(k, new byte[] { 0x01 })
                .Add(k + SlimDataInterpreter.TimeToLivePostfix, BitConverter.GetBytes(futureTicks)),
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(data);
        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
            .Returns(EmptyMeta());

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);

        await sut.CleanupOnceAsync(CancellationToken.None);

        db.VerifyNoOtherCalls();
        files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CleanupOnceAsync_deletes_expired_local_files_by_disk_metadata_on_every_node()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        state.Setup(s => s.Invoke()).Returns(new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        });

        var nowTicks = DateTime.UtcNow.Ticks;
        var expired = nowTicks - TimeSpan.TicksPerMinute;

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(Meta(new FileMetadataEntry("file1", new FileMetadata("application/octet-stream", "sha", 10, expired))));

        files.Setup(f => f.DeleteAsync("file1", It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);
        await sut.CleanupOnceAsync(CancellationToken.None);

        files.VerifyAll();
        db.VerifyNoOtherCalls();
*/
}

