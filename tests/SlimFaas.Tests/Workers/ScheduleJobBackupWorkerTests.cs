using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Database;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public class ScheduleJobBackupWorkerTests : IDisposable
{
    private readonly string _tempDir;
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

    private ScheduleJobBackupWorker CreateWorker(
        string? backupDirectory,
        bool coldStart,
        int backupIntervalSeconds = 3600,
        IServiceProvider? serviceProvider = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SlimDataOptions
        {
            BackupDirectory = backupDirectory,
            BackupIntervalSeconds = backupIntervalSeconds
        });

        var configValues = new Dictionary<string, string?>();
        if (coldStart) configValues["coldStart"] = "true";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new ScheduleJobBackupWorker(
            serviceProvider ?? BuildServiceProvider(),
            _slimDataStatus.Object,
            _masterService.Object,
            _logger.Object,
            options,
            configuration);
    }

    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_databaseService.Object);
        return services.BuildServiceProvider();
    }

    // ──────── Worker disabled ────────

    [Fact(DisplayName = "Worker désactivé quand BackupDirectory est null")]
    public async Task ExecuteAsync_Should_Return_Immediately_When_BackupDirectory_Is_Null()
    {
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
        var worker = CreateWorker(backupDirectory: "", coldStart: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        _slimDataStatus.Verify(s => s.WaitForReadyAsync(), Times.Never);
    }

    [Fact(DisplayName = "Worker appelle WaitForReadyAsync quand BackupDirectory est défini")]
    public async Task ExecuteAsync_Should_WaitForReady_When_BackupDirectory_Is_Set()
    {
        var worker = CreateWorker(backupDirectory: _tempDir, coldStart: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        // Assert — should have waited for Raft readiness
        _slimDataStatus.Verify(s => s.WaitForReadyAsync(), Times.Once);
    }

    // ──────── Restore skips ────────

    [Fact(DisplayName = "Restore ignoré quand pas en coldStart")]
    public async Task Restore_Should_Be_Skipped_When_Not_ColdStart()
    {
        var backupFile = Path.Combine(_tempDir, "schedule-jobs-backup.json");
        var backupData = new ScheduleJobBackupData
        {
            Hashsets = new() { ["ScheduleJob:test"] = new() { ["id"] = Convert.ToBase64String(new byte[] { 1 }) } }
        };
        await File.WriteAllTextAsync(backupFile,
            JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData));

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
        _masterService.SetupGet(m => m.IsMaster).Returns(false);
        var backupFile = Path.Combine(_tempDir, "schedule-jobs-backup.json");
        await File.WriteAllTextAsync(backupFile, """{"Hashsets":{"ScheduleJob:test":{"id":"AQI="}}}""");

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
        _masterService.SetupGet(m => m.IsMaster).Returns(true);

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

// ──────── Tests unitaires purs : hash + sérialisation ────────

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
        var backupFile = Path.Combine(_tempDir, "schedule-jobs-backup.json");
        var backupData = new ScheduleJobBackupData
        {
            Hashsets = new() { ["ScheduleJob:fib"] = new() { ["abc"] = Convert.ToBase64String(new byte[] { 10, 20, 30 }) } }
        };

        var tempPath = backupFile + ".tmp";
        var json = JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, backupFile, overwrite: true);

        Assert.True(File.Exists(backupFile));
        Assert.False(File.Exists(tempPath));
        var readBack = JsonSerializer.Deserialize(await File.ReadAllTextAsync(backupFile), ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        Assert.NotNull(readBack);
        Assert.Equal(Convert.ToBase64String(new byte[] { 10, 20, 30 }), readBack!.Hashsets["ScheduleJob:fib"]["abc"]);
    }

    [Fact(DisplayName = "Roundtrip préserve l'intégrité des données")]
    public async Task Roundtrip_File_Should_Preserve_Integrity()
    {
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
        var json = JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        await File.WriteAllTextAsync(filePath, json);

        var restored = JsonSerializer.Deserialize(await File.ReadAllTextAsync(filePath), ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        Assert.NotNull(restored);
        var dict = restored!.Hashsets["ScheduleJob:default"];
        Assert.Equal(originalBytes1, Convert.FromBase64String(dict["schedule-1"]));
        Assert.Equal(originalBytes2, Convert.FromBase64String(dict["schedule-2"]));
    }

    [Fact(DisplayName = "Restore d'un fichier vide ne produit aucune entrée")]
    public async Task Restore_Empty_Backup_Should_Produce_No_Entries()
    {
        var filePath = Path.Combine(_tempDir, "empty-backup.json");
        var emptyBackup = new ScheduleJobBackupData();
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(emptyBackup, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData));

        var restored = JsonSerializer.Deserialize(await File.ReadAllTextAsync(filePath), ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        Assert.NotNull(restored);
        Assert.Empty(restored!.Hashsets);
    }

    [Fact(DisplayName = "Backup avec plusieurs hashsets préserve toutes les clés")]
    public async Task Backup_Multiple_Hashsets_Should_Preserve_All_Keys()
    {
        var backupData = new ScheduleJobBackupData
        {
            Hashsets = new()
            {
                ["ScheduleJob:fibonacci"] = new() { ["id-1"] = Convert.ToBase64String(new byte[] { 1 }), ["id-2"] = Convert.ToBase64String(new byte[] { 2 }) },
                ["ScheduleJob:batch"] = new() { ["id-3"] = Convert.ToBase64String(new byte[] { 3 }) }
            }
        };

        var filePath = Path.Combine(_tempDir, "multi.json");
        var json = JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        await File.WriteAllTextAsync(filePath, json);
        var restored = JsonSerializer.Deserialize(await File.ReadAllTextAsync(filePath), ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Hashsets.Count);
        Assert.Equal(2, restored.Hashsets["ScheduleJob:fibonacci"].Count);
        Assert.Single(restored.Hashsets["ScheduleJob:batch"]);
    }

    [Fact(DisplayName = "SHA-256 identique si contenu identique")]
    public void Hash_Should_Be_Same_For_Same_Content()
    {
        var content = """{"Hashsets":{"ScheduleJob:test":{"id":"AQI="}}}""";
        var h1 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var h2 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        Assert.Equal(h1, h2);
    }

    [Fact(DisplayName = "SHA-256 différent si contenu différent")]
    public void Hash_Should_Differ_For_Different_Content()
    {
        var h1 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("""{"Hashsets":{}}""")));
        var h2 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("""{"Hashsets":{"ScheduleJob:x":{}}}""")));
        Assert.NotEqual(h1, h2);
    }
}
