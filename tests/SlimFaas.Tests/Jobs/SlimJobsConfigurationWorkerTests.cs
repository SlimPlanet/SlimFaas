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
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync()).Returns(Task.CompletedTask);

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 0));

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
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 0));

        // Act & Assert – must NOT throw
        var exception = await Record.ExceptionAsync(
            () => InvokeDoOneCycleAsync(worker, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact(DisplayName = "ExecuteAsync keeps calling SyncJobsConfigurationAsync until cancelled")]
    public async Task ExecuteAsync_LoopsUntilCancelled()
    {
        // Arrange – use a tiny delay so the loop spins a few times quickly
        var jobConfigMock = new Mock<IJobConfiguration>();
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync()).Returns(Task.CompletedTask);

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 10));

        using var cts = new CancellationTokenSource();

        // Act – run for a short time then cancel
        await worker.StartAsync(cts.Token);
        await Task.Delay(80);
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
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync()).Returns(Task.CompletedTask);

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 5));

        using var cts = new CancellationTokenSource();

        // Act
        await worker.StartAsync(cts.Token);
        cts.CancelAfter(50);

        // Should complete without throwing after cancellation
        var exception = await Record.ExceptionAsync(
            () => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact(DisplayName = "DoOneCycle: delay is respected (uses the configured milliseconds)")]
    public async Task DoOneCycle_UsesConfiguredDelay()
    {
        // Arrange – a notable delay
        var jobConfigMock = new Mock<IJobConfiguration>();
        jobConfigMock.Setup(c => c.SyncJobsConfigurationAsync()).Returns(Task.CompletedTask);

        var logger = NullLogger<SlimJobsConfigurationWorker>.Instance;
        var worker = new SlimJobsConfigurationWorker(
            jobConfigMock.Object, logger, CreateWorkersOptions(delayMs: 200));

        using var cts = new CancellationTokenSource(50); // cancel before delay elapses

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Act – the cycle should be cancelled during the Task.Delay, so Sync is never called
        await InvokeDoOneCycleAsync(worker, cts.Token);
        sw.Stop();

        // Assert – SyncJobsConfigurationAsync was NOT called because cancellation happened during delay
        jobConfigMock.Verify(c => c.SyncJobsConfigurationAsync(), Times.Never);
    }
}
