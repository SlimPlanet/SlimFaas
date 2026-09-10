using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Options;

namespace SlimFaas.Tests.Jobs;

/// <summary>
/// Tests for SlimJobsConfigurationWorker.
/// </summary>
public class SlimJobsConfigurationWorkerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static IOptions<SlimFaasOptions> CreateSlimFaasOptions() =>
        Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions());

    private static SlimFaas.Kubernetes.Watch.KubernetesWatchSignals DisabledSignals() => new();

    private static IOptions<WorkersOptions> CreateWorkersOptions(int delayMs = 0) =>
        Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            JobsConfigurationDelayMilliseconds = delayMs
        });

    private static Task InvokeDoOneCycleAsync(SlimJobsConfigurationWorker worker, CancellationToken token)
    {
        var method = typeof(SlimJobsConfigurationWorker)
            .GetMethod("DoOneCycle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(worker, new object[] { token })!;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "DoOneCycle calls SyncJobsConfigurationAsync once")]
    public async Task DoOneCycle_CallsSyncOnce()
    {
        // Arrange
        var jobConfigMock = new Mock<IJobConfiguration>();
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync()).ReturnsAsync(true);

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 0), CreateSlimFaasOptions(), DisabledSignals());

        // Act
        await InvokeDoOneCycleAsync(worker, CancellationToken.None);

        // Assert
        jobConfigMock.Verify(c => c.SyncJobsConfigurationAsync(), Times.Once);
    }

    [Fact(DisplayName = "DoOneCycle swallows exceptions and does not rethrow")]
    public async Task DoOneCycle_ExceptionInSync_IsSwallowed()
    {
        // Arrange
        var jobConfigMock = new Mock<IJobConfiguration>();
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync())
                     .ThrowsAsync(new InvalidOperationException("k8s unavailable"));

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 0), CreateSlimFaasOptions(), DisabledSignals());

        // Act & Assert – must NOT throw
        var exception = await Record.ExceptionAsync(
            () => InvokeDoOneCycleAsync(worker, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact(DisplayName = "ExecuteAsync keeps calling SyncJobsConfigurationAsync until cancelled")]
    public async Task ExecuteAsync_LoopsUntilCancelled()
    {
        // Arrange – use a tiny delay so the loop spins quickly
        var callCount = 0;
        var twoCallsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var jobConfigMock = new Mock<IJobConfiguration>();
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync())
            .Returns(() =>
            {
                if (Interlocked.Increment(ref callCount) >= 2)
                    twoCallsReached.TrySetResult();
                return Task.FromResult(true);
            });

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 10), CreateSlimFaasOptions(), DisabledSignals());

        using var cts = new CancellationTokenSource();

        // Act – wait until at least 2 calls are observed, then cancel
        await worker.StartAsync(cts.Token);
        await twoCallsReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        // Assert – was called at least a couple of times
        jobConfigMock.Verify(c => c.SyncJobsConfigurationAsync(), Times.AtLeast(2));
    }

    [Fact(DisplayName = "ExecuteAsync stops cleanly when CancellationToken is cancelled")]
    public async Task ExecuteAsync_CancelsCleanly()
    {
        // Arrange
        var jobConfigMock = new Mock<IJobConfiguration>();
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync()).ReturnsAsync(true);

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 5), CreateSlimFaasOptions(), DisabledSignals());

        using var cts = new CancellationTokenSource();

        // Act
        await worker.StartAsync(cts.Token);
        cts.CancelAfter(50);

        // Should complete without throwing after cancellation
        var exception = await Record.ExceptionAsync(
            () => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact(DisplayName = "DoOneCycle skips synchronization when its configured delay is cancelled")]
    public async Task DoOneCycle_CancellationDuringConfiguredDelay_SkipsSync()
    {
        var jobConfigMock = new Mock<IJobConfiguration>();
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync()).ReturnsAsync(true);

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: Timeout.Infinite), CreateSlimFaasOptions(), DisabledSignals());

        using var cts = new CancellationTokenSource();

        // An infinite configured delay lets us cancel at the await boundary without racing
        // two timers, whose continuations may run in either order on a busy CI worker.
        Task cycle = InvokeDoOneCycleAsync(worker, cts.Token);
        Assert.False(cycle.IsCompleted);
        cts.Cancel();
        await cycle.WaitAsync(TimeSpan.FromSeconds(10));

        jobConfigMock.Verify(c => c.SyncJobsConfigurationAsync(), Times.Never);
    }
}
