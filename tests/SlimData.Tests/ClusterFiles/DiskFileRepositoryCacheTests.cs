using Microsoft.Extensions.Logging;
using Moq;
using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class DiskFileRepositoryCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "slimdata_cache_tests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_local_body_with_valid_metadata_but_invalid_bytes_is_not_available(bool truncate)
    {
        var repository = CreateRepository(new TrackingCacheControl());
        using var content = new MemoryStream([1, 2, 3]);
        FilePutResult stored = await repository.SaveAsync(
            "damaged-body", content, "application/octet-stream", false, null, CancellationToken.None);
        string bodyPath = Path.Combine(_directory, Base64UrlCodec.Encode("damaged-body") + ".bin");
        await using (var body = new FileStream(bodyPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            if (truncate)
                body.SetLength(2);
            else
                await body.WriteAsync(new byte[] { 3, 2, 1 });
        }

        Assert.NotNull(await repository.TryGetMetadataAsync("damaged-body", CancellationToken.None));
        Assert.False(await repository.ExistsAsync("damaged-body", stored.Sha256Hex, CancellationToken.None));
    }

    [Fact]
    public async Task Readers_can_open_a_version_while_replacement_retains_its_write_handle()
    {
        var sut = CreateRepository(new TrackingCacheControl());
        using var content = new MemoryStream([1, 2, 3]);
        var put = await sut.SaveAsync("id", content, "application/octet-stream", true, null, CancellationToken.None);
        // ReplaceFile opens the replacement with write access. A concurrent
        // reader must share that access until the replacement call closes it.
        await using var replacementBody = new FileStream(Path.Combine(_directory, Base64UrlCodec.Encode("id") + ".bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        await using var replacementMetadata = new FileStream(Assert.Single(Directory.GetFiles(_directory, "*.meta.mp")),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        await using var reader = await sut.OpenReadAsync("id", CancellationToken.None);
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(put.Sha256Hex, (await sut.TryGetMetadataAsync("id", CancellationToken.None))!.Sha256Hex);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task A_download_with_the_wrong_expected_length_is_never_published(int expectedLength)
    {
        var repository = CreateRepository(new TrackingCacheControl());
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.ToArray()));

        await Assert.ThrowsAsync<IOException>(() => repository.SaveVerifiedAsync(
            "unpublished", content, new FilePutResult(hash, "text/plain", expectedLength), null, CancellationToken.None));

        Assert.Null(await repository.TryGetMetadataAsync("unpublished", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task A_verified_download_replaces_a_previous_version_after_validation(int length)
    {
        var repository = CreateRepository(new TrackingCacheControl());
        using var original = new MemoryStream(new byte[] { 9 });
        await repository.SaveAsync("replace", original, "text/plain", false, null, CancellationToken.None);
        byte[] expectedBytes = new byte[length];
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedBytes));
        using var content = new MemoryStream(expectedBytes);

        FilePutResult result = await repository.SaveVerifiedAsync("replace", content,
            new FilePutResult(hash, "application/octet-stream", length), null, CancellationToken.None);

        Assert.Equal(length, result.Length);
        Assert.True(await repository.ExistsAsync("replace", hash, CancellationToken.None));
        Assert.Equal("application/octet-stream", (await repository.TryGetMetadataAsync("replace", CancellationToken.None))!.ContentType);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp.*"));
    }

    [Fact]
    public async Task Open_readers_keep_their_version_when_the_id_is_replaced_and_deleted()
    {
        var sut = CreateRepository(new TrackingCacheControl());
        using var original = new MemoryStream([1, 2, 3]);
        await sut.SaveAsync("id", original, "application/octet-stream", true, null, CancellationToken.None);
        await using var oldReader = await sut.OpenReadAsync("id", CancellationToken.None);
        await using var oldMetadata = new FileStream(Assert.Single(Directory.GetFiles(_directory, "*.meta.mp")),
            FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        using var replacement = new MemoryStream([4, 5, 6, 7]);
        var updated = await sut.SaveAsync("id", replacement, "application/octet-stream", true, null, CancellationToken.None);
        Assert.Equal(updated.Sha256Hex, (await sut.TryGetMetadataAsync("id", CancellationToken.None))!.Sha256Hex);
        await using var newReader = await sut.OpenReadAsync("id", CancellationToken.None);
        await sut.DeleteAsync("id", CancellationToken.None);

        using var oldBytes = new MemoryStream();
        using var newBytes = new MemoryStream();
        await oldReader.CopyToAsync(oldBytes);
        await newReader.CopyToAsync(newBytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, oldBytes.ToArray());
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, newBytes.ToArray());
        Assert.Null(await sut.TryGetMetadataAsync("id", CancellationToken.None));
    }

    [Fact]
    public async Task Save_and_synchronous_read_dispose_drop_cache_once_each()
    {
        var cache = new TrackingCacheControl();
        var sut = CreateRepository(cache);

        await sut.SaveAsync(
            "id",
            new MemoryStream([1, 2, 3]),
            "application/octet-stream",
            overwrite: true,
            expireAtUtcTicks: null,
            CancellationToken.None);
        Assert.Equal(1, cache.Calls);

        var stream = await sut.OpenReadAsync("id", CancellationToken.None);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        stream.Dispose();
        stream.Dispose();

        Assert.Equal(2, cache.Calls);
        Assert.All(cache.StreamWasOpen, Assert.True);
    }

    [Fact]
    public async Task Asynchronous_read_dispose_drops_cache_once()
    {
        var cache = new TrackingCacheControl();
        var sut = CreateRepository(cache);
        await sut.SaveAsync(
            "id",
            new MemoryStream([1]),
            "application/octet-stream",
            overwrite: true,
            expireAtUtcTicks: null,
            CancellationToken.None);

        var stream = await sut.OpenReadAsync("id", CancellationToken.None);
        await stream.DisposeAsync();
        await stream.DisposeAsync();

        Assert.Equal(2, cache.Calls);
        Assert.All(cache.StreamWasOpen, Assert.True);
    }

    [Fact]
    public void Disabled_linux_cache_control_is_a_no_op()
    {
        var logger = new Mock<ILogger<DiskFileRepository>>().Object;
        var control = new LinuxFileCacheControl(enabled: false, logger);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "file.bin");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        control.Drop(stream);

        Assert.False(stream.SafeFileHandle.IsClosed);
    }

    private DiskFileRepository CreateRepository(IFileCacheControl cache)
    {
        Directory.CreateDirectory(_directory);
        return new DiskFileRepository(
            _directory,
            new Mock<ILogger<DiskFileRepository>>().Object,
            cache);
    }

    private sealed class TrackingCacheControl : IFileCacheControl
    {
        public int Calls { get; private set; }
        public List<bool> StreamWasOpen { get; } = [];

        public void Drop(FileStream stream)
        {
            Calls++;
            StreamWasOpen.Add(!stream.SafeFileHandle.IsClosed);
        }
    }
}
