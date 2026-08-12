using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Kubernetes.Watch;
using SlimFaas.Options;

namespace SlimFaas.Tests.Jobs;

// Watch-driven behavior of the jobs workers: the jobs list synchronization only runs
// when the Jobs signal fired or the resync interval elapsed; the CronJob
// configuration sync is pulse-driven with a resync fallback.
public class SlimJobsWorkersWatchTests
{
    private static readonly TimeSpan AssertTimeout = TimeSpan.FromSeconds(10);

    private static SlimJobsWorker CreateJobsWorker(
        Mock<IJobService> jobService,
        KubernetesWatchSignals signals,
        int jobsResyncSeconds)
    {
        var jobConfiguration = new Mock<IJobConfiguration>();
        jobConfiguration.SetupGet(c => c.Configuration)
            .Returns(new SlimFaasJobConfiguration(new Dictionary<string, SlimfaasJob>()));
        var slimDataStatus = new Mock<ISlimDataStatus>();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);
        var masterService = new Mock<IMasterService>();
        masterService.SetupGet(m => m.IsMaster).Returns(false);

        return new SlimJobsWorker(
            new Mock<IJobQueue>().Object,
            jobService.Object,
            jobConfiguration.Object,
            NullLogger<SlimJobsWorker>.Instance,
            new HistoryHttpMemoryService(),
            slimDataStatus.Object,
            masterService.Object,
            new Mock<IReplicasService>().Object,
            Microsoft.Extensions.Options.Options.Create(new WorkersOptions { JobsDelayMilliseconds = 20 }),
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                KubernetesWatch = new KubernetesWatchOptions { JobsResyncSeconds = jobsResyncSeconds }
            }),
            signals);
    }

    private static Mock<IJobService> CreateJobService(Action? onSync = null)
    {
        var jobService = new Mock<IJobService>();
        jobService.Setup(s => s.SyncJobsAsync())
            .Callback(() => onSync?.Invoke())
            .ReturnsAsync(new List<Job>());
        jobService.SetupGet(s => s.Jobs).Returns(new List<Job>());
        return jobService;
    }

    [Fact]
    public async Task JobsSyncRunsOnceThenReusesTheCachedListWhenWatchIsEnabled()
    {
        var signals = new KubernetesWatchSignals { WatchEnabled = true };
        var firstSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobService = CreateJobService(() => firstSync.TrySetResult());
        SlimJobsWorker worker = CreateJobsWorker(jobService, signals, jobsResyncSeconds: 3600);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await firstSync.Task.WaitAsync(AssertTimeout);
            // Laisse tourner plusieurs cycles (20 ms chacun) sans événement.
            await Task.Delay(500);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        jobService.Verify(s => s.SyncJobsAsync(), Times.Once);
        jobService.VerifyGet(s => s.Jobs, Times.AtLeastOnce);
    }

    [Fact]
    public async Task JobsSignalPulseTriggersAResync()
    {
        var signals = new KubernetesWatchSignals { WatchEnabled = true };
        int syncCount = 0;
        var secondSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobService = CreateJobService(() =>
        {
            if (Interlocked.Increment(ref syncCount) >= 2)
            {
                secondSync.TrySetResult();
            }
        });
        SlimJobsWorker worker = CreateJobsWorker(jobService, signals, jobsResyncSeconds: 3600);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            signals.Jobs.Pulse();
            await secondSync.Task.WaitAsync(AssertTimeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(Volatile.Read(ref syncCount) >= 2);
    }

    [Fact]
    public async Task JobsSyncRunsEveryCycleWhenWatchIsDisabled()
    {
        var signals = new KubernetesWatchSignals { WatchEnabled = false };
        int syncCount = 0;
        var thirdSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobService = CreateJobService(() =>
        {
            if (Interlocked.Increment(ref syncCount) >= 3)
            {
                thirdSync.TrySetResult();
            }
        });
        SlimJobsWorker worker = CreateJobsWorker(jobService, signals, jobsResyncSeconds: 3600);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await thirdSync.Task.WaitAsync(AssertTimeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(Volatile.Read(ref syncCount) >= 3);
    }

    // ── SlimJobsConfigurationWorker ──────────────────────────────────────────────

    private static SlimJobsConfigurationWorker CreateConfigurationWorker(
        Mock<IJobConfiguration> jobConfiguration,
        KubernetesWatchSignals signals,
        int resyncSeconds)
        => new(
            jobConfiguration.Object,
            NullLogger<SlimJobsConfigurationWorker>.Instance,
            Microsoft.Extensions.Options.Options.Create(new WorkersOptions { JobsConfigurationDelayMilliseconds = 0 }),
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                KubernetesWatch = new KubernetesWatchOptions { JobsConfigurationResyncSeconds = resyncSeconds }
            }),
            signals);

    private static Task InvokeDoOneCycleAsync(SlimJobsConfigurationWorker worker, CancellationToken token)
    {
        var method = typeof(SlimJobsConfigurationWorker)
            .GetMethod("DoOneCycle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(worker, new object[] { token })!;
    }

    [Fact]
    public async Task ConfigurationSyncIsPulseDrivenWhenWatchIsEnabled()
    {
        var signals = new KubernetesWatchSignals { WatchEnabled = true };
        var jobConfiguration = new Mock<IJobConfiguration>();
        jobConfiguration.Setup(c => c.SyncJobsConfigurationAsync()).Returns(Task.CompletedTask);
        // Resync très long : seul un pulse peut débloquer le cycle.
        SlimJobsConfigurationWorker worker = CreateConfigurationWorker(jobConfiguration, signals, resyncSeconds: 3600);

        Task cycle = InvokeDoOneCycleAsync(worker, CancellationToken.None);
        await Task.Delay(300);
        Assert.False(cycle.IsCompleted);
        jobConfiguration.Verify(c => c.SyncJobsConfigurationAsync(), Times.Never);

        signals.JobsConfiguration.Pulse();
        await cycle.WaitAsync(AssertTimeout);

        jobConfiguration.Verify(c => c.SyncJobsConfigurationAsync(), Times.Once);
    }

    [Fact]
    public async Task ConfigurationSyncFallsBackToResyncWithoutAnyPulse()
    {
        var signals = new KubernetesWatchSignals { WatchEnabled = true };
        var jobConfiguration = new Mock<IJobConfiguration>();
        jobConfiguration.Setup(c => c.SyncJobsConfigurationAsync()).Returns(Task.CompletedTask);
        SlimJobsConfigurationWorker worker = CreateConfigurationWorker(jobConfiguration, signals, resyncSeconds: 1);

        await InvokeDoOneCycleAsync(worker, CancellationToken.None).WaitAsync(AssertTimeout);

        jobConfiguration.Verify(c => c.SyncJobsConfigurationAsync(), Times.Once);
    }
}
