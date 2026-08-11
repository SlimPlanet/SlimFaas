using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Tests.PerfRegression;

// Non-regression test protecting the "no backup file rewrite when nothing changed"
// behavior ("recurring worker cost" theme), whatever the internal change-detection
// mechanism (hash of the serialized JSON or incremental hash of the raw data).
public class ScheduleJobBackupSkipRegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _stateDir;

    public ScheduleJobBackupSkipRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slimfaas-backup-skip-" + Guid.NewGuid().ToString("N"));
        _stateDir = Path.Combine(_tempDir, "state");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_stateDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task BackupFileIsNotRewrittenWhenNothingChanged()
    {
        var slimDataStatus = new Mock<ISlimDataStatus>();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(new SlimPersistentState(_stateDir));
        services.AddSingleton<IDatabaseService>(new Mock<IDatabaseService>().Object);

        var worker = new ScheduleJobBackupWorker(
            services.BuildServiceProvider(),
            slimDataStatus.Object,
            new Mock<IMasterService>().Object,
            new Mock<ILogger<ScheduleJobBackupWorker>>().Object,
            Microsoft.Extensions.Options.Options.Create(new SlimDataOptions
            {
                BackupDirectory = _tempDir,
                BackupIntervalSeconds = 1
            }),
            new ConfigurationBuilder().Build());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var backupFile = Path.Combine(_tempDir, "schedule-jobs-backup.json");

            // Wait for the initial write.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(backupFile) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(File.Exists(backupFile), "the initial backup must be written");
            DateTime initialWriteTime = File.GetLastWriteTimeUtc(backupFile);

            // Let at least two backup cycles pass: the state has not changed, so the
            // file must not be rewritten.
            await Task.Delay(2500);

            Assert.Equal(initialWriteTime, File.GetLastWriteTimeUtc(backupFile));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
