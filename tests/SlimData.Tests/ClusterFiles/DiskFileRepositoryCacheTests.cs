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
