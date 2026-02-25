using Microsoft.Extensions.Logging;
using Moq;
using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class DiskFileRepositoryCleanupTests : IDisposable
{
    private readonly string _dir;
    private readonly DiskFileRepository _sut;

    public DiskFileRepositoryCleanupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "slimdata_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sut = new DiskFileRepository(_dir, new Mock<ILogger<DiskFileRepository>>().Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string CreateTmpFile(string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "orphan");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CleanupOrphanTempFilesAsync_deletes_old_tmp_files()
    {
        // Arrange – orphaned .tmp file older than 11 minutes
        var oldTmp = CreateTmpFile("abc.bin.tmp.deadbeef", DateTime.UtcNow.AddMinutes(-11));

        // Act
        var deleted = await _sut.CleanupOrphanTempFilesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldTmp), "The old .tmp file should have been deleted.");
    }

    [Fact]
    public async Task CleanupOrphanTempFilesAsync_keeps_recent_tmp_files()
    {
        // Arrange – recent .tmp file (5 minutes): must NOT be deleted
        var recentTmp = CreateTmpFile("abc.bin.tmp.cafebabe", DateTime.UtcNow.AddMinutes(-5));

        // Act
        var deleted = await _sut.CleanupOrphanTempFilesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(recentTmp), "The recent .tmp file must not be deleted.");
    }

    [Fact]
    public async Task CleanupOrphanTempFilesAsync_deletes_only_old_tmp_files_among_several()
    {
        // Arrange
        var old1 = CreateTmpFile("file1.bin.tmp.aaa", DateTime.UtcNow.AddMinutes(-15));
        var old2 = CreateTmpFile("file2.meta.mp.tmp.bbb", DateTime.UtcNow.AddMinutes(-60));
        var recent = CreateTmpFile("file3.bin.tmp.ccc", DateTime.UtcNow.AddMinutes(-3));

        // Act
        var deleted = await _sut.CleanupOrphanTempFilesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, deleted);
        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task CleanupOrphanTempFilesAsync_returns_zero_when_no_tmp_files()
    {
        // Arrange – no .tmp files in the directory
        File.WriteAllText(Path.Combine(_dir, "normal.bin"), "data");

        // Act
        var deleted = await _sut.CleanupOrphanTempFilesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task CleanupOrphanTempFilesAsync_respects_cancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.CleanupOrphanTempFilesAsync(cts.Token));
    }

    [Fact]
    public async Task CleanupOrphanTempFilesAsync_does_not_delete_normal_bin_files()
    {
        // Arrange – normal binary file (not .tmp): must never be deleted
        var normalFile = Path.Combine(_dir, "regularfile.bin");
        File.WriteAllText(normalFile, "content");
        File.SetLastWriteTimeUtc(normalFile, DateTime.UtcNow.AddHours(-2));

        // Act
        var deleted = await _sut.CleanupOrphanTempFilesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(normalFile));
    }
}

