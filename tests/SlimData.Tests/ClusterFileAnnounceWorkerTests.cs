using Microsoft.Extensions.Logging;
using Moq;
using SlimData.ClusterFiles;
using Xunit;

public sealed class ClusterFileAnnounceWorkerTests
{
    [Fact]
    public async Task Worker_pulls_when_missing_and_disposes_stream()
    {
        var queue = new ClusterFileAnnounceQueue();

        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var sync = new Mock<IClusterFileSync>(MockBehavior.Strict);
        var logger = new Mock<ILogger<ClusterFileAnnounceWorker>>();

        var id = "id1";
        var sha = "sha1";

        // file missing locally
        repo.Setup(r => r.ExistsAsync(id, sha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // track stream disposal
        var disposedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TrackingStream(() => disposedTcs.TrySetResult());

        sync.Setup(s => s.PullFileIfMissingAsync(id, sha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilePullResult(stream));

        var worker = new ClusterFileAnnounceWorker(queue, sync.Object, repo.Object, logger.Object);

        await worker.StartAsync(CancellationToken.None);

        Assert.True(queue.TryEnqueue(new AnnouncedFile(id, sha)));

        // wait until stream disposed => implies pull happened and worker disposed it
        await disposedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await worker.StopAsync(CancellationToken.None);

        repo.VerifyAll();
        sync.VerifyAll();
    }

    [Fact]
    public async Task Worker_does_not_pull_when_already_present()
    {
        var queue = new ClusterFileAnnounceQueue();

        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var sync = new Mock<IClusterFileSync>(MockBehavior.Strict);
        var logger = new Mock<ILogger<ClusterFileAnnounceWorker>>();

        var id = "id1";
        var sha = "sha1";

        // file already present => no pull
        repo.Setup(r => r.ExistsAsync(id, sha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var worker = new ClusterFileAnnounceWorker(queue, sync.Object, repo.Object, logger.Object);

        await worker.StartAsync(CancellationToken.None);

        Assert.True(queue.TryEnqueue(new AnnouncedFile(id, sha)));

        // small delay to allow worker to process
        await Task.Delay(150);

        await worker.StopAsync(CancellationToken.None);

        repo.VerifyAll();
        sync.VerifyNoOtherCalls();
    }

    private sealed class TrackingStream : MemoryStream
    {
        private readonly Action _onDispose;

        public TrackingStream(Action onDispose)
        {
            _onDispose = onDispose;
            WriteByte(0x01);
            Position = 0;
        }

        protected override void Dispose(bool disposing)
        {
            try { _onDispose(); } catch { /* ignore */ }
            base.Dispose(disposing);
        }
    }
}
