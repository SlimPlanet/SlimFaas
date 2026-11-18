using DotNext.Net.Cluster.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class ClusterFileVersionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slimdata_file_versions_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Publishing_a_replacement_preserves_the_body_still_named_by_earlier_raft_metadata()
    {
        var repository = new DiskFileRepository(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);
        using var original = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult before = await repository.SaveAsync("mutable-file", original, "text/plain", false, null, CancellationToken.None);
        using var replacement = new MemoryStream(new byte[] { 4, 5, 6 });
        FilePutResult after = await repository.SaveAsync("mutable-file", replacement, "text/plain", true, null, CancellationToken.None);

        // Body publication precedes the caller's Raft metadata commit. The old
        // reference must remain usable during that interval, including on a peer.
        Assert.True(await repository.ExistsAsync("mutable-file", before.Sha256Hex, CancellationToken.None));
        Assert.True(await repository.ExistsAsync("mutable-file", after.Sha256Hex, CancellationToken.None));
    }

    [Fact]
    public async Task A_pull_returns_the_requested_hash_while_a_new_revision_is_being_admitted()
    {
        var repository = new DiskFileRepository(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);
        using var original = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult before = await repository.SaveAsync("mutable-file", original, "text/plain", false, null, CancellationToken.None);
        using var replacement = new MemoryStream(new byte[] { 4, 5, 6 });
        await repository.SaveAsync("mutable-file", replacement, "text/plain", true, null, CancellationToken.None);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        bus.SetupGet(value => value.Members).Returns(Array.Empty<ISubscriber>());
        bus.Setup(value => value.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(value => value.RemoveListener(It.IsAny<IInputChannel>()));
        await using var sync = new ClusterFileSync(bus.Object, repository, new ClusterFileAnnounceQueue(),
            Mock.Of<ILogger<ClusterFileSync>>(), Mock.Of<IHttpClientFactory>());

        FilePullResult result = await sync.PullFileIfMissingAsync("mutable-file", before.Sha256Hex, null, CancellationToken.None);

        Assert.NotNull(result.Stream);
        await using Stream retained = result.Stream!;
        using var bytes = new MemoryStream();
        await retained.CopyToAsync(bytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
    }

    [Fact]
    public async Task Deleting_an_obsolete_revision_keeps_the_current_body_and_legacy_reader_path()
    {
        var repository = new DiskFileRepository(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);
        using var original = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult before = await repository.SaveAsync("file", original, "text/plain", false, null, CancellationToken.None);
        using var replacement = new MemoryStream(new byte[] { 4, 5, 6 });
        FilePutResult after = await repository.SaveAsync("file", replacement, "text/plain", true, null, CancellationToken.None);

        await repository.DeleteVersionAsync("file", before.Sha256Hex, CancellationToken.None);

        Assert.False(await repository.ExistsAsync("file", before.Sha256Hex, CancellationToken.None));
        Assert.True(await repository.ExistsAsync("file", after.Sha256Hex, CancellationToken.None));
        string legacyPath = Path.Combine(_directory, Base64UrlCodec.Encode("file") + ".bin");
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(legacyPath));
    }

    [Fact]
    public async Task Replacing_a_legacy_layout_preserves_its_revision_and_current_path()
    {
        var repository = new DiskFileRepository(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);
        using var original = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult before = await repository.SaveAsync("legacy", original, "text/plain", false, null, CancellationToken.None);
        // Keep exactly the historical body + MemoryPack metadata layout.
        Assert.False(Directory.Exists(Path.Combine(_directory, ".versions")));
        Assert.True(File.Exists(Path.Combine(_directory, Base64UrlCodec.Encode("legacy") + ".bin")));
        Assert.True(await repository.ExistsAsync("legacy", before.Sha256Hex, CancellationToken.None));
        using var replacement = new MemoryStream(new byte[] { 4, 5, 6 });

        FilePutResult after = await repository.SaveAsync("legacy", replacement, "text/plain", true, null, CancellationToken.None);

        Assert.True(await repository.ExistsAsync("legacy", before.Sha256Hex, CancellationToken.None));
        Assert.True(await repository.ExistsAsync("legacy", after.Sha256Hex, CancellationToken.None));
        string legacyPath = Path.Combine(_directory, Base64UrlCodec.Encode("legacy") + ".bin");
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(legacyPath));
    }

    [Fact]
    public async Task Expired_revision_metadata_temporaries_are_cleaned_without_removing_recent_transfers()
    {
        var repository = new DiskFileRepository(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);
        string versions = Path.Combine(_directory, ".versions");
        Directory.CreateDirectory(versions);
        string stale = Path.Combine(versions, "revision.meta.mp.tmp.stale");
        string recent = Path.Combine(versions, "revision.meta.mp.tmp.recent");
        await File.WriteAllTextAsync(stale, "partial metadata");
        await File.WriteAllTextAsync(recent, "active transfer");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-20));

        Assert.Equal(1, await repository.CleanupOrphanTempFilesAsync(CancellationToken.None));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }
}
