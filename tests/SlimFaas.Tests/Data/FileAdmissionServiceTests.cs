using System.Net;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Messaging;
using MemoryPack;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimData.Options;
using SlimFaas;

namespace SlimFaas.Tests.Data;

public sealed class FileAdmissionServiceTests
{
    private const string Id = "sf-internal-0123456789abcdef0123456789abcdef-internal";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LocalVolume = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RemoteVolume = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task No_queue_publication_is_possible_before_the_remote_persistence_receipt()
    {
        var fixture = new Fixture();
        var gate = new TaskCompletionSource<FileCopyReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sync.Setup(value => value.ConfirmRemoteCopyAsync(Id, Hash, 3, It.IsAny<CancellationToken>())).Returns(gate.Task);
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        Task<PreparedFileAdmission> prepare = fixture.Service.PrepareAsync(Id, content, "text/plain", Id, 3, null, CancellationToken.None);
        Assert.False(prepare.IsCompleted);
        Assert.Equal(FileAdmissionAction.Begin, Assert.Single(fixture.Mutations).Action);
        gate.SetResult(fixture.Receipt());
        PreparedFileAdmission ticket = await prepare;
        byte[] packet = MemoryPackSerializer.Serialize(new CustomRequest([], null, "billing", "/", "POST", "", Id));
        await fixture.Service.CommitQueueAsync(ticket, "billing", "delivery", packet, new([1], 30, [503]), CancellationToken.None);
        Assert.Equal(new[] { FileAdmissionAction.Begin, FileAdmissionAction.CommitQueue }, fixture.Mutations.Select(value => value.Action));
        var commit = fixture.Mutations[1];
        Assert.Equal(packet, commit.Value);
        Assert.Equal("Queue:billing", commit.QueueKey);
        Assert.Equal("delivery", commit.ElementId);
        Assert.Equal(2, commit.Copies.Length);
        Assert.Equal(2, commit.Copies.Select(value => value.StorageIdentity).Distinct().Count());
        Assert.True(FileAdmissionTags.TryRead(ticket.Metadata.Tags!, out string token, out long deadline));
        Assert.Equal(ticket.Token, token); Assert.Equal(ticket.DeadlineUtcTicks, deadline);
        Assert.Equal(Id, MemoryPackSerializer.Deserialize<DataSetMetadata>(commit.Metadata)!.Tags![DataFileKeys.StorageIdTag]);
        Assert.Equal(7, packet[0]);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("id")]
    [InlineData("volume")]
    [InlineData("not-persisted")]
    [InlineData("unknown-node")]
    [InlineData("admission-token")]
    public async Task Missing_or_mismatched_remote_receipt_prevents_acceptance(string defect)
    {
        var fixture = new Fixture();
        var setup = fixture.Sync.Setup(value => value.ConfirmRemoteCopyAsync(Id, Hash, 3, It.IsAny<CancellationToken>()));
        if (defect == "unavailable") setup.ThrowsAsync(new IOException("peer unavailable"));
        else setup.ReturnsAsync(() =>
        {
            var receipt = fixture.Receipt();
            return defect switch
            {
                "hash" => receipt with { Sha256Hex = new string('b', 64) },
                "length" => receipt with { Length = 4 },
                "id" => receipt with { Id = "other" },
                "volume" => receipt with { StorageIdentity = LocalVolume },
                "not-persisted" => receipt with { Persisted = false },
                "unknown-node" => receipt with { NodeIdentity = "unknown" },
                "admission-token" => receipt with { AdmissionToken = Guid.NewGuid().ToString("N") },
                _ => receipt
            };
        });
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<SlimDataUnavailableException>(() => fixture.Service.PrepareAsync(Id, content, "text/plain", Id, 3, null, CancellationToken.None));
        Assert.Equal(FileAdmissionAction.Begin, Assert.Single(fixture.Mutations).Action);
        fixture.Database.Verify(value => value.SetQueueMetadataAsync(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task Loss_of_the_confirmed_member_before_publication_is_rejected()
    {
        var fixture = new Fixture();
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var ticket = await fixture.Service.PrepareAsync(Id, content, "text/plain", Id, 3, null, CancellationToken.None);
        fixture.Members = [fixture.Members[0]];
        await Assert.ThrowsAsync<SlimDataUnavailableException>(() => fixture.Service.CommitQueueAsync(ticket, "billing", "delivery", [1], new([], 30, []), CancellationToken.None));
        Assert.Single(fixture.Mutations);
    }

    [Fact]
    public async Task Local_only_cost_variant_persists_locally_and_publishes_a_unique_physical_alias()
    {
        var fixture = new Fixture(copies: 1);
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var ticket = await fixture.Service.PrepareAsync(Id, content, "text/plain", "download.txt", 3, null, CancellationToken.None);
        await fixture.Service.CommitPublicAsync(ticket, "public-name", 60000, CancellationToken.None);
        fixture.Repository.Verify(value => value.PersistVersionAsync(Id, Hash, 3, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Sync.Verify(value => value.ConfirmRemoteCopyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        var commit = fixture.Mutations[1];
        Assert.Equal(1, fixture.Mutations[0].RequiredCopies);
        Assert.Equal(DataFileKeys.MetaKey("public-name"), commit.AliasKey);
        Assert.Equal(DataFileKeys.MetaKey(Id), commit.MetadataKey);
        Assert.NotEqual(commit.AliasKey, commit.MetadataKey);
        Assert.Equal(commit.Metadata, commit.AliasValue);
        Assert.True(commit.AliasExpireAtUtcTicks > DateTime.UtcNow.Ticks);
    }

    [Fact]
    public async Task Rejected_begin_does_not_write_a_body()
    {
        var fixture = new Fixture();
        fixture.Database.Setup(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>()))
            .ReturnsAsync(new FileAdmissionResult { Error = "expired" });
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<SlimDataUnavailableException>(() => fixture.Service.PrepareAsync(Id, content, "text/plain", Id, 3, null, CancellationToken.None));
        fixture.Sync.Verify(value => value.BroadcastFilePutAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<CancellationToken>(), It.IsAny<IDictionary<string, string>?>()), Times.Never);
    }

    private sealed class Fixture
    {
        public Mock<IDatabaseService> Database { get; } = new();
        public Mock<IDurableFileRepository> Repository { get; } = new();
        public Mock<IDurableClusterFileSync> Sync { get; } = new();
        public List<FileAdmissionMutation> Mutations { get; } = [];
        public ISubscriber[] Members { get; set; } = [Member(5001, false), Member(5002, true), Member(5003, true)];
        public FileAdmissionService Service { get; }
        private string? _admissionToken;
        public Fixture(int copies = 2)
        {
            Database.Setup(value => value.ExecuteFileAdmissionAsync(It.IsAny<FileAdmissionMutation>()))
                .ReturnsAsync((FileAdmissionMutation mutation) =>
                {
                    Mutations.Add(mutation);
                    return new() { Applied = true, Phase = mutation.Action == FileAdmissionAction.Begin ? FileAdmissionPhase.Pending : FileAdmissionPhase.Active };
                });
            Repository.SetupGet(value => value.StorageIdentity).Returns(LocalVolume);
            Repository.Setup(value => value.PersistVersionAsync(Id, Hash, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FilePersistenceReceipt(Id, Hash, 3, LocalVolume));
            Sync.Setup(value => value.BroadcastFilePutAsync(Id, It.IsAny<Stream>(), "text/plain", 3, false, null,
                It.IsAny<CancellationToken>(), It.IsAny<IDictionary<string, string>?>()))
                .Callback<string, Stream, string, long, bool, long?, CancellationToken, IDictionary<string, string>?>((_, _, _, _, _, _, _, tags) =>
                    _admissionToken = tags![FileAdmissionTags.Token])
                .ReturnsAsync(new FilePutResult(Hash, "text/plain", 3));
            Sync.Setup(value => value.ConfirmRemoteCopyAsync(Id, Hash, 3, It.IsAny<CancellationToken>())).ReturnsAsync(() => Receipt());
            var bus = new Mock<IMessageBus>(); bus.SetupGet(value => value.Members).Returns(() => Members);
            Service = new(Database.Object, Sync.Object, Repository.Object, bus.Object, Microsoft.Extensions.Options.Options.Create(new ClusterFileOptions { RequiredDurableCopies = copies }));
        }

        public FileCopyReceipt Receipt() => new(Id, Hash, 3, Members[1].Id.ToString(), RemoteVolume, true, AdmissionToken: _admissionToken);
        private static ISubscriber Member(int port, bool remote)
        {
            var member = new Mock<ISubscriber>();
            member.SetupGet(value => value.IsRemote).Returns(remote);
            member.SetupGet(value => value.Id).Returns(ClusterMemberId.FromEndPoint(new IPEndPoint(IPAddress.Loopback, port)));
            return member.Object;
        }
    }
}
