using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using DotNext;
using MemoryPack;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimData.Expiration;
using SlimFaas;

namespace SlimFaas.Tests.Workers;

public sealed class FileAdmissionCleanupTests
{
    private const string TokenTag = "SlimFaasFileAdmissionToken";
    private const string DeadlineTag = "SlimFaasFileAdmissionDeadlineUtcTicks";
    private const string Id = "admitted-physical-file";
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly long Future = DateTime.UtcNow.AddMinutes(2).Ticks;
    private static readonly long Past = DateTime.UtcNow.AddMinutes(-2).Ticks;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_pin_and_stale_follower_state_cannot_justify_deleting_an_admitted_body(bool staleFollower)
    {
        var entry = new FileAdmissionEntry
        {
            FileId = Id, Token = Token, Phase = FileAdmissionPhase.Pending, DeadlineUtcTicks = Future
        };
        var payload = Payload(staleFollower ? null : entry);
        var state = new Mock<ISupplier<SlimDataPayload>>(); state.Setup(value => value.Invoke()).Returns(payload);
        var files = Files(staleFollower ? Past : Future);
        var db = new Mock<IDatabaseService>();
        db.Setup(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>()))
            .ReturnsAsync(new FileAdmissionResult { Error = "The leader still has a live owner." });
        var cleaner = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, NullLogger<SlimDataExpirationCleaner>.Instance);
        for (int index = 0; index < 4; index++) await cleaner.CleanupOnceAsync(CancellationToken.None);
        files.Verify(value => value.DeleteVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        if (staleFollower)
            db.Verify(value => value.ExecuteFileAdmissionAsync(It.Is<FileAdmissionMutation>(mutation =>
                mutation.Action == FileAdmissionAction.Retire && mutation.FileId == Id && mutation.Token == Token &&
                mutation.DeadlineUtcTicks == Past)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Expired_managed_alias_does_not_use_an_unconditional_delete_from_a_stale_TTL_snapshot()
    {
        string key = DataFileKeys.MetaKey("public-name");
        var tags = new Dictionary<string, string> { [TokenTag] = Token, [DeadlineTag] = Past.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var payload = Payload(null);
        payload.KeyValues = payload.KeyValues.Add(key, MemoryPackSerializer.Serialize(new DataSetMetadata("sha", 42, "text/plain", "name", tags)))
            .Add(SlimDataInterpreter.TtlKey(key), BitConverter.GetBytes(Past));
        var state = new Mock<ISupplier<SlimDataPayload>>(); state.Setup(value => value.Invoke()).Returns(payload);
        var db = new Mock<IDatabaseService>(); var files = Files(Past);
        db.Setup(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>())).ReturnsAsync(new FileAdmissionResult());
        var cleaner = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, NullLogger<SlimDataExpirationCleaner>.Instance);
        for (int index = 0; index < 4; index++) await cleaner.CleanupOnceAsync(CancellationToken.None);
        db.Verify(value => value.DeleteAsync(key), Times.Never);
        files.Verify(value => value.DeleteVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Durable_pending_index_protects_a_local_copy_even_if_its_marker_is_missing()
    {
        var entry = new FileAdmissionEntry { FileId = Id, Token = Token, Phase = FileAdmissionPhase.Pending, DeadlineUtcTicks = Future };
        var state = new Mock<ISupplier<SlimDataPayload>>(); state.Setup(value => value.Invoke()).Returns(Payload(entry));
        var files = Files(Future);
        files.Setup(value => value.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>())).Returns(Metadata(new Dictionary<string, string>()));
        var cleaner = new SlimDataExpirationCleaner(state.Object, Mock.Of<IDatabaseService>(), files.Object, NullLogger<SlimDataExpirationCleaner>.Instance);
        for (int index = 0; index < 4; index++) await cleaner.CleanupOnceAsync(CancellationToken.None);
        files.Verify(value => value.DeleteVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_confirmed_retirement_allows_local_deletion(bool retired)
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(); state.Setup(value => value.Invoke()).Returns(Payload(null));
        var files = Files(Past); var db = new Mock<IDatabaseService>();
        db.Setup(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>())).ReturnsAsync(new FileAdmissionResult
        {
            Applied = true, Phase = retired ? FileAdmissionPhase.Retired : FileAdmissionPhase.Active
        });
        var cleaner = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, NullLogger<SlimDataExpirationCleaner>.Instance);
        await cleaner.CleanupOnceAsync(CancellationToken.None);
        files.Verify(value => value.DeleteVersionAsync(Id, "sha", It.IsAny<CancellationToken>()), retired ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task Managed_offload_descriptor_is_retired_atomically_instead_of_deleted_by_legacy_orphan_heuristics()
    {
        var entry = new FileAdmissionEntry { FileId = Id, Token = Token, Phase = FileAdmissionPhase.Active, ElementId = "original", QueueKey = "Queue:billing" };
        var payload = Payload(entry);
        var tags = new Dictionary<string, string> { [TokenTag] = Token, ["QueueElementId"] = "original" };
        payload.KeyValues = payload.KeyValues.Add(DataFileKeys.MetaKey(Id),
            MemoryPackSerializer.Serialize(new DataSetMetadata("sha", 42, "text/plain", Id, tags)));
        var state = new Mock<ISupplier<SlimDataPayload>>(); state.Setup(value => value.Invoke()).Returns(payload);
        var files = Files(Past); var db = new Mock<IDatabaseService>();
        db.Setup(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>())).ReturnsAsync(new FileAdmissionResult());
        var cleaner = new SlimDataExpirationCleaner(state.Object, db.Object, files.Object, NullLogger<SlimDataExpirationCleaner>.Instance);
        for (int index = 0; index < 4; index++) await cleaner.CleanupOnceAsync(CancellationToken.None);
        db.Verify(value => value.DeleteAsync(DataFileKeys.MetaKey(Id)), Times.Never);
        files.Verify(value => value.DeleteVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SlimDataPayload Payload(FileAdmissionEntry? entry) => new()
    {
        KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty,
        Hashsets = entry is null ? ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty :
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty.Add(FileAdmissionStore.IndexKey,
                ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add(Id, MemoryPackSerializer.Serialize(entry)))
    };

    private static Mock<IFileRepository> Files(long deadline)
    {
        var files = new Mock<IFileRepository>();
        var tags = new Dictionary<string, string>
        {
            [TokenTag] = Token, [DeadlineTag] = deadline.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        files.Setup(value => value.EnumerateAllMetadataAsync(It.IsAny<CancellationToken>())).Returns(Metadata(tags));
        files.Setup(value => value.CleanupOrphanTempFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        return files;
    }

    private static async IAsyncEnumerable<FileMetadataEntry> Metadata(IDictionary<string, string> tags,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return new(Id, new("text/plain", "sha", 42, null, tags));
        await Task.CompletedTask;
    }
}
