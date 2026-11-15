using System.Collections.Immutable;
using DotNext;
using MemoryPack;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimData.Options;
using SlimFaas;

namespace SlimFaas.Tests.Data;

public sealed class FileAdmissionReconcilerTests
{
    [Fact]
    public async Task A_node_with_no_historical_announcements_repairs_a_referenced_body()
    {
        var fixture = new Fixture(); using var reconciler = fixture.Reconciler();
        Assert.Equal(1, await reconciler.ReconcileOnceAsync(CancellationToken.None));
        fixture.Sync.Verify(value => value.PullFileIfMissingAsync("a", "sha", null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Repository.Verify(value => value.PersistVersionAsync("a", "sha", 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_existing_announcement_copy_is_also_persisted_before_counting_as_restored_redundancy()
    {
        var fixture = new Fixture();
        fixture.Repository.Setup(value => value.ExistsAsync("a", "sha", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var reconciler = fixture.Reconciler(); await reconciler.ReconcileOnceAsync(CancellationToken.None);
        fixture.Repository.Verify(value => value.PersistVersionAsync("a", "sha", 4, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Sync.Verify(value => value.PullFileIfMissingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Byte_budget_and_cursor_allow_large_files_to_progress_without_starving_the_next_reference()
    {
        var fixture = new Fixture("a", "b"); using var reconciler = fixture.Reconciler(maxBytes: 1);
        Assert.Equal(1, await reconciler.ReconcileOnceAsync(CancellationToken.None));
        Assert.Equal(1, await reconciler.ReconcileOnceAsync(CancellationToken.None));
        fixture.Sync.Verify(value => value.PullFileIfMissingAsync("a", "sha", null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Sync.Verify(value => value.PullFileIfMissingAsync("b", "sha", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Interrupted_repair_is_revisited_on_the_next_round()
    {
        var fixture = new Fixture();
        fixture.Sync.SetupSequence(value => value.PullFileIfMissingAsync("a", "sha", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("interrupted")).ReturnsAsync(() => new FilePullResult(new MemoryStream(new byte[] { 1, 2, 3, 4 })));
        using var reconciler = fixture.Reconciler();
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        fixture.Sync.Verify(value => value.PullFileIfMissingAsync("a", "sha", null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Repository.Verify(value => value.PersistVersionAsync("a", "sha", 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Durable_DLQ_owner_keeps_a_body_eligible_for_repair_after_the_executable_queue_is_empty()
    {
        var fixture = new Fixture();
        fixture.Payload.Queues = fixture.Payload.Queues.Clear();
        fixture.Payload.Hashsets = fixture.Payload.Hashsets.Add(DeadLetterKeys.Messages("billing"),
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add("dlq",
                MemoryPackSerializer.Serialize(new DeadLetterEntry { Id = "dlq", OriginalElementId = "a", Function = "billing" })));
        using var reconciler = fixture.Reconciler();
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        fixture.Sync.Verify(value => value.PullFileIfMissingAsync("a", "sha", null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Database.Verify(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>()), Times.Never);
    }

    [Fact]
    public async Task Retired_body_is_removed_before_forgetting_and_is_never_pulled()
    {
        var fixture = new Fixture();
        fixture.Payload.Queues = fixture.Payload.Queues.Clear();
        var order = new List<string>();
        fixture.Database.Setup(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>()))
            .ReturnsAsync((FileAdmissionMutation mutation) =>
            {
                order.Add(mutation.Action.ToString());
                return new() { Applied = true, Phase = FileAdmissionPhase.Retired };
            });
        fixture.Sync.Setup(value => value.DeleteLocalAsync("a", It.IsAny<CancellationToken>())).Callback(() => order.Add("delete")).Returns(Task.CompletedTask);
        using var reconciler = fixture.Reconciler();
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "Retire", "delete", "Forget" }, order);
        fixture.Sync.Verify(value => value.PullFileIfMissingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_rejected_retirement_or_failed_read_barrier_never_deletes_a_body()
    {
        var fixture = new Fixture(); fixture.Payload.Queues = fixture.Payload.Queues.Clear();
        fixture.Database.Setup(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>())).ReturnsAsync(new FileAdmissionResult());
        using var reconciler = fixture.Reconciler();
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        fixture.Database.Setup(value => value.GetAsync(It.IsAny<string>())).ThrowsAsync(new SlimDataUnavailableException("no quorum"));
        await Assert.ThrowsAsync<SlimDataUnavailableException>(() => reconciler.ReconcileOnceAsync(CancellationToken.None));
        fixture.Sync.Verify(value => value.DeleteLocalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Legacy_empty_index_does_not_add_background_Raft_reads()
    {
        var fixture = new Fixture(); fixture.Payload.Hashsets = fixture.Payload.Hashsets.Clear();
        using var reconciler = fixture.Reconciler();
        Assert.Equal(0, await reconciler.ReconcileOnceAsync(CancellationToken.None));
        fixture.Database.Verify(value => value.GetAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_file_metadata_without_an_admission_index_is_also_reconciled(bool expired)
    {
        var fixture = new Fixture(); fixture.Payload.Hashsets = fixture.Payload.Hashsets.Clear();
        fixture.Payload.KeyValues = fixture.Payload.KeyValues.Add(DataFileKeys.MetaKey("legacy-public"),
            MemoryPackSerializer.Serialize(new DataSetMetadata("sha", 4, "text/plain", "legacy")));
        if (expired) fixture.Payload.KeyValues = fixture.Payload.KeyValues.Add(SlimDataInterpreter.TtlKey(DataFileKeys.MetaKey("legacy-public")),
            BitConverter.GetBytes(DateTime.UtcNow.AddMinutes(-1).Ticks));
        using var reconciler = fixture.Reconciler(); await reconciler.ReconcileOnceAsync(CancellationToken.None);
        fixture.Sync.Verify(value => value.PullFileIfMissingAsync("legacy-public", "sha", null, It.IsAny<CancellationToken>()),
            expired ? Times.Never : Times.Once);
    }

    private sealed class Fixture
    {
        public SlimDataPayload Payload;
        public Mock<IDatabaseService> Database { get; } = new();
        public Mock<IDurableFileRepository> Repository { get; } = new();
        public Mock<IClusterFileSync> Sync { get; } = new();
        private readonly Mock<ISupplier<SlimDataPayload>> _state = new();
        public Fixture(params string[] ids)
        {
            if (ids.Length == 0) ids = ["a"];
            var entries = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;
            var queue = ImmutableArray<QueueElement>.Empty;
            foreach (string id in ids)
            {
                var entry = new FileAdmissionEntry
                {
                    FileId = id, Token = Guid.NewGuid().ToString("N"), DeadlineUtcTicks = DateTime.UtcNow.AddMinutes(-1).Ticks,
                    Phase = FileAdmissionPhase.Active, QueueKey = "Queue:billing", ElementId = id, Sha256Hex = "sha", Length = 4
                };
                entries = entries.Add(id, MemoryPackSerializer.Serialize(entry));
                queue = queue.Add(new(new byte[] { 1 }, id, 1, 30, [], [], []));
            }
            Payload = new()
            {
                KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
                Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty.Add(FileAdmissionStore.IndexKey, entries),
                Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty.Add("Queue:billing", queue)
            };
            _state.Setup(value => value.Invoke()).Returns(() => Payload);
            Database.Setup(value => value.GetAsync(It.IsAny<string>())).ReturnsAsync((byte[]?)null);
            Sync.Setup(value => value.PullFileIfMissingAsync(It.IsAny<string>(), "sha", null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new FilePullResult(new MemoryStream(new byte[] { 1, 2, 3, 4 })));
            Repository.Setup(value => value.PersistVersionAsync(It.IsAny<string>(), "sha", 4, It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, string hash, long length, CancellationToken _) => new FilePersistenceReceipt(id, hash, length, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Repository.SetupGet(value => value.StorageIdentity).Returns("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        }

        public FileAdmissionReconciler Reconciler(long maxBytes = 32 * 1024 * 1024) =>
            new(Database.Object, _state.Object, Sync.Object, Repository.Object,
                Microsoft.Extensions.Options.Options.Create(new ClusterFileOptions { ReconcileMaxBytes = maxBytes }), NullLogger<FileAdmissionReconciler>.Instance);
    }
}
