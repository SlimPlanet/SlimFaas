using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using DotNext;
using MemoryPack;
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
    private static async IAsyncEnumerable<FileMetadataEntry> EmptyMeta(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    private static async IAsyncEnumerable<FileMetadataEntry> Meta(
        IEnumerable<FileMetadataEntry> entries,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task CleanupOnceAsync_deletes_expired_keyvalue_baseKey_and_deletes_expired_local_file_by_disk_metadata()
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

        // step 1: ttlKey expired => delete baseKey (= metaKey)
        db.Setup(d => d.DeleteAsync(metaKey))
          .Returns(Task.CompletedTask);

        // step 3: local disk cleanup from .meta.json
        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(Meta(new[]
             {
                 new FileMetadataEntry(
                     Id: "abc",
                     Metadata: new FileMetadata("application/octet-stream", "sha", 10, expiredTicks))
             }));

        files.Setup(f => f.DeleteAsync("abc", It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        files.Setup(f => f.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(0);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);

        await sut.CleanupOnceAsync(CancellationToken.None);

        db.VerifyAll();
        files.VerifyAll();

        // non-expired key must not be deleted
        db.Verify(d => d.DeleteAsync("k1"), Times.Never);
        db.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CleanupOnceAsync_deletes_expired_hashset_when___ttl__field_is_expired()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        var nowTicks = DateTime.UtcNow.Ticks;
        var expiredTicks = nowTicks - TimeSpan.TicksPerSecond;

        var key = "hs:myset";

        var hs = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
            .Add(key, ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                .Add(SlimDataInterpreter.HashsetTtlField, BitConverter.GetBytes(expiredTicks))
                .Add("value", new byte[] { 0x01 }));

        var data = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Hashsets = hs,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(data);

        db.Setup(d => d.HashSetDeleteAsync(key, ""))
          .Returns(Task.CompletedTask);

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(EmptyMeta());

        files.Setup(f => f.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(0);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);

        await sut.CleanupOnceAsync(CancellationToken.None);

        db.VerifyAll();
        files.VerifyAll();
        db.VerifyNoOtherCalls();
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
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
                .Add("hs:alive", ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                    .Add(SlimDataInterpreter.HashsetTtlField, BitConverter.GetBytes(futureTicks))),
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(data);

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(EmptyMeta());

        files.Setup(f => f.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(0);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);

        await sut.CleanupOnceAsync(CancellationToken.None);

        // no delete expected
        db.VerifyNoOtherCalls();
        files.VerifyAll(); // EnumerateAllMetadataAsync must be called
        files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CleanupOnceAsync_deletes_expired_local_files_by_disk_metadata()
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
        var future = nowTicks + TimeSpan.TicksPerMinute;

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(Meta(new[]
             {
                 new FileMetadataEntry("file-expired", new FileMetadata("application/octet-stream", "sha1", 10, expired)),
                 new FileMetadataEntry("file-future",  new FileMetadata("application/octet-stream", "sha2", 10, future)),
                 new FileMetadataEntry("file-no-ttl",  new FileMetadata("application/octet-stream", "sha3", 10, null)),
             }));

        files.Setup(f => f.DeleteAsync("file-expired", It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        files.Setup(f => f.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(0);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);

        await sut.CleanupOnceAsync(CancellationToken.None);

        files.VerifyAll();
        db.VerifyNoOtherCalls();

        // must only delete the expired one
        files.Verify(f => f.DeleteAsync("file-future", It.IsAny<CancellationToken>()), Times.Never);
        files.Verify(f => f.DeleteAsync("file-no-ttl", It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static QueueElement CreateQueueElement(string id) => new(
        value: ReadOnlyMemory<byte>.Empty,
        id: id,
        insertTimeStamp: 0,
        httpTimeoutSeconds: 30,
        timeoutRetriesSeconds: ImmutableArray<int>.Empty,
        retryQueueElements: ImmutableArray<QueueHttpTryElement>.Empty,
        httpStatusRetries: ImmutableHashSet<int>.Empty);

    private static ReadOnlyMemory<byte> SerializeOffloadMeta(string queueElementId)
    {
        var meta = new DataSetMetadata(
            Sha256Hex: "sha",
            Length: 100,
            ContentType: "application/octet-stream",
            FileName: "file.bin",
            Tags: new Dictionary<string, string> { ["QueueElementId"] = queueElementId });
        return MemoryPackSerializer.Serialize(meta);
    }

    // ── orphan RAFT key (section 4) tests ──────────────────────────────────────

    [Fact]
    public async Task CleanupOnceAsync_deletes_orphaned_raft_offload_metadata_when_queue_element_is_gone()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        const string fileId = "orphan-raft-file";
        const string metaKey = "data:file:" + fileId + ":meta";
        const string queueElementId = "elem-orphan";

        var data = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                .Add(metaKey, SerializeOffloadMeta(queueElementId)),
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty  // empty – element is gone
        };

        state.Setup(s => s.Invoke()).Returns(data);

        db.Setup(d => d.DeleteAsync(metaKey)).Returns(Task.CompletedTask);

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(EmptyMeta());
        files.Setup(f => f.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);
        await sut.CleanupOnceAsync(CancellationToken.None);

        db.VerifyAll();
        db.VerifyNoOtherCalls();
        files.VerifyAll();
        files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CleanupOnceAsync_does_not_delete_raft_offload_metadata_when_queue_element_is_still_active()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        const string fileId = "active-raft-file";
        const string metaKey = "data:file:" + fileId + ":meta";
        const string queueElementId = "elem-active";

        var data = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                .Add(metaKey, SerializeOffloadMeta(queueElementId)),
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
                .Add("q1", ImmutableArray.Create(CreateQueueElement(queueElementId)))  // still in queue
        };

        state.Setup(s => s.Invoke()).Returns(data);

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(EmptyMeta());
        files.Setup(f => f.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);
        await sut.CleanupOnceAsync(CancellationToken.None);

        db.VerifyNoOtherCalls();
        files.VerifyAll();
        files.VerifyNoOtherCalls();
    }

    // ── orphan disk file (section 3) tests ─────────────────────────────────────

    [Fact]
    public async Task CleanupOnceAsync_deletes_orphaned_disk_offload_file_when_queue_element_is_gone()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        const string fileId = "orphan-disk-file";
        const string queueElementId = "disk-elem-orphan";

        var data = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty  // empty – element is gone
        };

        state.Setup(s => s.Invoke()).Returns(data);

        var diskMeta = new FileMetadata(
            ContentType: "application/octet-stream",
            Sha256Hex: "sha",
            Length: 100,
            ExpireAtUtcTicks: null,  // no TTL expiry
            Tags: new Dictionary<string, string> { ["QueueElementId"] = queueElementId });

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(Meta(new[] { new FileMetadataEntry(fileId, diskMeta) }));
        files.Setup(f => f.DeleteAsync(fileId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        files.Setup(f => f.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);
        await sut.CleanupOnceAsync(CancellationToken.None);

        files.VerifyAll();
        db.VerifyNoOtherCalls();
        files.Verify(f => f.DeleteAsync(fileId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupOnceAsync_does_not_delete_disk_offload_file_when_queue_element_is_still_active()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);
        var files = new Mock<IFileRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<SlimDataExpirationCleaner>>();

        const string fileId = "active-disk-file";
        const string queueElementId = "disk-elem-active";

        var data = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
                .Add("q1", ImmutableArray.Create(CreateQueueElement(queueElementId)))  // still active
        };

        state.Setup(s => s.Invoke()).Returns(data);

        var diskMeta = new FileMetadata(
            ContentType: "application/octet-stream",
            Sha256Hex: "sha",
            Length: 100,
            ExpireAtUtcTicks: null,
            Tags: new Dictionary<string, string> { ["QueueElementId"] = queueElementId });

        files.Setup(f => f.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>()))
             .Returns(Meta(new[] { new FileMetadataEntry(fileId, diskMeta) }));
        files.Setup(f => f.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var sut = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, logger.Object);
        await sut.CleanupOnceAsync(CancellationToken.None);

        db.VerifyNoOtherCalls();
        files.VerifyAll();
        files.Verify(f => f.DeleteAsync(fileId, It.IsAny<CancellationToken>()), Times.Never);
        files.VerifyNoOtherCalls();
    }
}
