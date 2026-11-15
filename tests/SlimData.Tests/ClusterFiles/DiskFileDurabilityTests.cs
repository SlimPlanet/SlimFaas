using Microsoft.Extensions.Logging.Abstractions;
using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class DiskFileDurabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slimdata_durability_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_save_waits_for_the_directory_barrier_before_returning_success()
    {
        using var durability = new TrackingDurability();
        var repository = new DiskFileRepository(_directory, NullLogger<DiskFileRepository>.Instance, new NoCache(), durability);
        durability.BlockDirectory = true;
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        Task<FilePutResult> save = Task.Run(() => repository.SaveAsync("file", content, "text/plain", false, null, CancellationToken.None));
        try
        {
            await durability.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(save.IsCompleted);
            Assert.True(durability.FileFlushes >= 2);
        }
        finally
        {
            durability.Release.Set();
        }
        await save;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_persistence_barrier_is_not_reported_as_a_successful_replacement(bool failFile)
    {
        using var durability = new TrackingDurability();
        var repository = new DiskFileRepository(_directory, NullLogger<DiskFileRepository>.Instance, new NoCache(), durability);
        using var original = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult previous = await repository.SaveAsync("file", original, "text/plain", false, null, CancellationToken.None);
        durability.FailFile = failFile;
        durability.FailDirectory = !failFile;
        using var replacement = new MemoryStream(new byte[] { 4, 5, 6 });

        await Assert.ThrowsAsync<IOException>(() => repository.SaveAsync("file", replacement, "text/plain", true, null, CancellationToken.None));

        Assert.True(await repository.ExistsAsync("file", previous.Sha256Hex, CancellationToken.None));
    }

    [Fact]
    public async Task Real_durable_writes_complete_the_host_file_and_directory_barriers()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var repository = new DiskFileRepository(_directory, NullLogger<DiskFileRepository>.Instance,
            dropPageCache: false, durableWrites: true);
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });

        FilePutResult put = await repository.SaveAsync("file", content, "text/plain", false, null, CancellationToken.None);

        Assert.True(await repository.ExistsAsync("file", put.Sha256Hex, CancellationToken.None));
    }

    private sealed class NoCache : IFileCacheControl
    {
        public void Drop(FileStream stream) { }
    }

    private sealed class TrackingDurability : IFileDurability, IDisposable
    {
        public bool Enabled => true;
        public bool BlockDirectory { get; set; }
        public bool FailFile { get; set; }
        public bool FailDirectory { get; set; }
        public int FileFlushes { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public void FlushFile(FileStream stream)
        {
            FileFlushes++;
            if (FailFile)
                throw new IOException("Injected file flush failure");
        }

        public void FlushDirectory(string path)
        {
            if (FailDirectory)
                throw new IOException("Injected directory flush failure");
            if (BlockDirectory)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new IOException("The test did not release its directory barrier");
            }
        }

        public void Dispose() => Release.Dispose();
    }
}
