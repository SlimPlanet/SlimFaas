using System.Collections.Immutable;
using System.Text.Json;
using DotNext;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas.Database;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public class ScheduleJobBackupWorkerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IScheduleJobBackupNotifier> _notifier = new();
    private readonly Mock<ISlimDataStatus> _slimDataStatus = new();
    private readonly Mock<IMasterService> _masterService = new();
    private readonly Mock<IDatabaseService> _databaseService = new();
    private readonly Mock<ILogger<ScheduleJobBackupWorker>> _logger = new();

    public ScheduleJobBackupWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slimfaas-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ──────── Helper : construire un worker avec options ────────

    private ScheduleJobBackupWorker CreateWorker(
        string? backupDirectory,
        bool coldStart,
        IServiceProvider? serviceProvider = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SlimDataOptions
        {
            BackupDirectory = backupDirectory
        });

        var configValues = new Dictionary<string, string?>();
        if (coldStart)
            configValues["coldStart"] = "true";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new ScheduleJobBackupWorker(
            _notifier.Object,
            serviceProvider ?? BuildServiceProvider(ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty),
            _slimDataStatus.Object,
            _masterService.Object,
            _logger.Object,
            options,
            configuration);
    }

    private IServiceProvider BuildServiceProvider(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>> hashsets)
    {
        var payload = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty,
            Hashsets = hashsets
        };

        var mockSupplier = new Mock<ISupplier<SlimDataPayload>>();
        mockSupplier.Setup(s => s.Invoke()).Returns(payload);

        // SlimPersistentState is sealed and hard to mock.
        // We build a ServiceCollection with both IDatabaseService and
        // a mock ISupplier<SlimDataPayload>. The worker resolves
        // SlimPersistentState, which implements ISupplier<SlimDataPayload>.
        // Since we can't mock the sealed class, we register the mock as
        // ISupplier<SlimDataPayload> directly and use a custom approach.
        //
        // But the worker does:
        //   _serviceProvider.GetRequiredService<SlimPersistentState>()
        //   (ISupplier<SlimDataPayload>)provider
        //
        // So we need to register a fake that is both SlimPersistentState-typed
        // and ISupplier<SlimDataPayload>. Since SlimPersistentState is sealed,
        // we'll use a wrapper approach: register IDatabaseService and
        // ISupplier<SlimDataPayload> and override the worker's usage.
        //
        // Actually, the simplest approach: make a real ServiceProvider with mock.

        var services = new ServiceCollection();
        services.AddSingleton(_databaseService.Object);

        // We can't directly register SlimPersistentState, so we'll test
        // backup/restore via file I/O and the methods that don't need it.
        // For full integration, we'd need a real SlimPersistentState.
        // Instead, we test the worker's behavior at a higher level.

        return services.BuildServiceProvider();
    }

    // ──────── Tests : Worker disabled ────────

    [Fact(DisplayName = "Worker désactivé quand BackupDirectory est null")]
    public async Task ExecuteAsync_Should_Return_Immediately_When_BackupDirectory_Is_Null()
    {
        // Arrange
        var worker = CreateWorker(backupDirectory: null, coldStart: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        // Assert — should not have called WaitForReadyAsync
        _slimDataStatus.Verify(s => s.WaitForReadyAsync(), Times.Never);
    }

    [Fact(DisplayName = "Worker désactivé quand BackupDirectory est vide")]
    public async Task ExecuteAsync_Should_Return_Immediately_When_BackupDirectory_Is_Empty()
    {
        // Arrange
        var worker = CreateWorker(backupDirectory: "", coldStart: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        _slimDataStatus.Verify(s => s.WaitForReadyAsync(), Times.Never);
    }

    [Fact(DisplayName = "Worker activé quand BackupDirectory est défini")]
    public async Task ExecuteAsync_Should_WaitForReady_When_BackupDirectory_Is_Set()
    {
        // Arrange
        _notifier.Setup(n => n.WaitForChangeAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                var tcs = new TaskCompletionSource<bool>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return new ValueTask<bool>(tcs.Task);
            });

        var worker = CreateWorker(backupDirectory: _tempDir, coldStart: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        // Assert — should have waited for Raft readiness
        _slimDataStatus.Verify(s => s.WaitForReadyAsync(), Times.Once);
    }

    // ──────── Tests : Restore skips ────────

    [Fact(DisplayName = "Restore ignoré quand pas en coldStart")]
    public async Task Restore_Should_Be_Skipped_When_Not_ColdStart()
    {
        // Arrange — write a backup file (should be ignored)
        var backupFile = Path.Combine(_tempDir, "schedule-jobs-backup.json");
        var backupData = new ScheduleJobBackupData
        {
            Hashsets = new() { ["ScheduleJob:test"] = new() { ["id"] = Convert.ToBase64String(new byte[] { 1 }) } }
        };
        await File.WriteAllTextAsync(backupFile,
            JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData));

        _notifier.Setup(n => n.WaitForChangeAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                var tcs = new TaskCompletionSource<bool>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return new ValueTask<bool>(tcs.Task);
            });

        var worker = CreateWorker(backupDirectory: _tempDir, coldStart: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        // Assert — no restore should have happened
        _databaseService.Verify(d => d.HashSetAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, byte[]>>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact(DisplayName = "Restore ignoré quand pas master")]
    public async Task Restore_Should_Be_Skipped_When_Not_Master()
    {
        // Arrange
        _masterService.SetupGet(m => m.IsMaster).Returns(false);

        var backupFile = Path.Combine(_tempDir, "schedule-jobs-backup.json");
        await File.WriteAllTextAsync(backupFile, """{"Hashsets":{"ScheduleJob:test":{"id":"AQI="}}}""");

        _notifier.Setup(n => n.WaitForChangeAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                var tcs = new TaskCompletionSource<bool>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return new ValueTask<bool>(tcs.Task);
            });

        var worker = CreateWorker(backupDirectory: _tempDir, coldStart: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(3000);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        _databaseService.Verify(d => d.HashSetAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, byte[]>>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact(DisplayName = "Restore ignoré quand le fichier backup n'existe pas")]
    public async Task Restore_Should_Be_Skipped_When_No_Backup_File()
    {
        // Arrange
        _masterService.SetupGet(m => m.IsMaster).Returns(true);

        _notifier.Setup(n => n.WaitForChangeAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                var tcs = new TaskCompletionSource<bool>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return new ValueTask<bool>(tcs.Task);
            });

        var worker = CreateWorker(backupDirectory: _tempDir, coldStart: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(3000);
        await worker.StopAsync(CancellationToken.None);

        // Assert — no data to restore
        _databaseService.Verify(d => d.HashSetAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, byte[]>>(), It.IsAny<long?>()), Times.Never);
    }
}

// ──────── Tests unitaires purs pour la sérialisation backup/restore ────────

public class ScheduleJobBackupFileTests : IDisposable
{
    private readonly string _tempDir;

    public ScheduleJobBackupFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slimfaas-backup-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact(DisplayName = "Écriture atomique : le fichier final existe après écriture")]
    public async Task Atomic_Write_Should_Produce_Final_File()
    {
        // Arrange
        var backupFile = Path.Combine(_tempDir, "schedule-jobs-backup.json");
        var backupData = new ScheduleJobBackupData
        {
            Hashsets = new()
            {
                ["ScheduleJob:fib"] = new()
                {
                    ["abc"] = Convert.ToBase64String(new byte[] { 10, 20, 30 })
                }
            }
        };

        // Act — simulate atomic write (same logic as worker)
        var tempPath = backupFile + ".tmp";
        var json = JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, backupFile, overwrite: true);

        // Assert
        Assert.True(File.Exists(backupFile));
        Assert.False(File.Exists(tempPath));

        var readBack = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(backupFile),
            ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        Assert.NotNull(readBack);
        Assert.Single(readBack!.Hashsets);
        Assert.Equal(Convert.ToBase64String(new byte[] { 10, 20, 30 }), readBack.Hashsets["ScheduleJob:fib"]["abc"]);
    }

    [Fact(DisplayName = "Roundtrip : backup data → fichier → restore data préserve l'intégrité")]
    public async Task Roundtrip_File_Should_Preserve_Integrity()
    {
        // Arrange
        var originalBytes1 = new byte[] { 1, 2, 3, 4, 5 };
        var originalBytes2 = new byte[] { 100, 200 };

        var backupData = new ScheduleJobBackupData
        {
            Hashsets = new()
            {
                ["ScheduleJob:default"] = new()
                {
                    ["schedule-1"] = Convert.ToBase64String(originalBytes1),
                    ["schedule-2"] = Convert.ToBase64String(originalBytes2)
                }
            }
        };

        var filePath = Path.Combine(_tempDir, "test-roundtrip.json");

        // Act — write
        var json = JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        await File.WriteAllTextAsync(filePath, json);

        // Act — read back
        var readJson = await File.ReadAllTextAsync(filePath);
        var restored = JsonSerializer.Deserialize(readJson, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        // Assert
        Assert.NotNull(restored);
        var restoredDict = restored!.Hashsets["ScheduleJob:default"];
        Assert.Equal(originalBytes1, Convert.FromBase64String(restoredDict["schedule-1"]));
        Assert.Equal(originalBytes2, Convert.FromBase64String(restoredDict["schedule-2"]));
    }

    [Fact(DisplayName = "Restore d'un fichier vide ne produit aucune entrée")]
    public async Task Restore_Empty_Backup_Should_Produce_No_Entries()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "empty-backup.json");
        var emptyBackup = new ScheduleJobBackupData();
        var json = JsonSerializer.Serialize(emptyBackup, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        await File.WriteAllTextAsync(filePath, json);

        // Act
        var readJson = await File.ReadAllTextAsync(filePath);
        var restored = JsonSerializer.Deserialize(readJson, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        // Assert
        Assert.NotNull(restored);
        Assert.Empty(restored!.Hashsets);
    }

    [Fact(DisplayName = "Backup avec plusieurs hashsets ScheduleJob préserve toutes les clés")]
    public async Task Backup_Multiple_Hashsets_Should_Preserve_All_Keys()
    {
        // Arrange
        var backupData = new ScheduleJobBackupData
        {
            Hashsets = new()
            {
                ["ScheduleJob:fibonacci"] = new()
                {
                    ["id-1"] = Convert.ToBase64String(new byte[] { 1 }),
                    ["id-2"] = Convert.ToBase64String(new byte[] { 2 })
                },
                ["ScheduleJob:batch"] = new()
                {
                    ["id-3"] = Convert.ToBase64String(new byte[] { 3 })
                }
            }
        };

        var filePath = Path.Combine(_tempDir, "multi.json");

        // Act
        var json = JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        await File.WriteAllTextAsync(filePath, json);
        var restored = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(filePath),
            ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        // Assert
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Hashsets.Count);
        Assert.Equal(2, restored.Hashsets["ScheduleJob:fibonacci"].Count);
        Assert.Single(restored.Hashsets["ScheduleJob:batch"]);
    }
}

