using Microsoft.Extensions.Logging.Abstractions;
using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class FilePersistenceReceiptTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slimdata_receipt_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_persistence_receipt_matches_actual_bytes_and_survives_repository_restart()
    {
        if (!Supported())
            return;
        var repository = CreateRepository(_directory);
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult put = await repository.SaveAsync("file", content, "text/plain", false, null, CancellationToken.None);

        FilePersistenceReceipt receipt = await repository.PersistVersionAsync("file", put.Sha256Hex, put.Length, CancellationToken.None);
        var restarted = CreateRepository(_directory);

        Assert.Equal("file", receipt.Id);
        Assert.Equal(put.Sha256Hex, receipt.Sha256Hex);
        Assert.Equal(3, receipt.Length);
        Assert.Equal(repository.StorageIdentity, receipt.StorageIdentity);
        Assert.Equal(receipt.StorageIdentity, restarted.StorageIdentity);
    }

    [Fact]
    public void Repositories_on_the_same_declared_volume_share_one_storage_identity()
    {
        if (!Supported())
            return;
        var first = CreateRepository(Path.Combine(_directory, "node-a", "files"), _directory);
        var second = CreateRepository(Path.Combine(_directory, "node-b", "files"), _directory);

        Assert.Equal(first.StorageIdentity, second.StorageIdentity);
    }

    [Fact]
    public void Distinct_storage_roots_have_distinct_persisted_identities()
    {
        if (!Supported())
            return;
        var first = CreateRepository(Path.Combine(_directory, "volume-a"));
        var second = CreateRepository(Path.Combine(_directory, "volume-b"));

        Assert.NotEqual(first.StorageIdentity, second.StorageIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_or_truncated_bytes_cannot_produce_a_persistence_receipt(bool truncate)
    {
        if (!Supported())
            return;
        var repository = CreateRepository(_directory);
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult put = await repository.SaveAsync("file", content, "text/plain", false, null, CancellationToken.None);
        string body = Path.Combine(_directory, Base64UrlCodec.Encode("file") + ".bin");
        await using (var damaged = new FileStream(body, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            if (truncate)
                damaged.SetLength(2);
            else
                await damaged.WriteAsync(new byte[] { 3, 2, 1 });
        }

        await Assert.ThrowsAsync<IOException>(() => repository.PersistVersionAsync("file", put.Sha256Hex, put.Length, CancellationToken.None));
    }

    [Fact]
    public async Task An_earlier_revision_receipt_does_not_identify_the_newer_current_body()
    {
        if (!Supported())
            return;
        var repository = CreateRepository(_directory);
        using var original = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult before = await repository.SaveAsync("file", original, "text/plain", false, null, CancellationToken.None);
        using var replacement = new MemoryStream(new byte[] { 4, 5, 6 });
        FilePutResult after = await repository.SaveAsync("file", replacement, "text/plain", true, null, CancellationToken.None);

        FilePersistenceReceipt receipt = await repository.PersistVersionAsync("file", before.Sha256Hex, before.Length, CancellationToken.None);

        Assert.Equal(before.Sha256Hex, receipt.Sha256Hex);
        Assert.NotEqual(after.Sha256Hex, receipt.Sha256Hex);
    }

    private static bool Supported() => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static DiskFileRepository CreateRepository(string path, string? storageRoot = null) =>
        new(path, NullLogger<DiskFileRepository>.Instance, dropPageCache: false, storageRoot: storageRoot);
}
