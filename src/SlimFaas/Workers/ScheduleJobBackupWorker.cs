using System.Text.Json;
using DotNext;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.Commands;
using SlimFaas.Database;
using SlimFaas.Options;

namespace SlimFaas.Workers;

/// <summary>
/// Background worker that:
///   1. On startup in coldStart mode with an empty database, restores ScheduleJob data from the backup volume.
///   2. Listens to <see cref="IScheduleJobBackupNotifier"/> signals and backs up all ScheduleJob hashsets to a JSON file.
/// All nodes perform the same backup; only the master performs the restore.
/// </summary>
public sealed class ScheduleJobBackupWorker : BackgroundService
{
    private readonly IScheduleJobBackupNotifier _notifier;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISlimDataStatus _slimDataStatus;
    private readonly IMasterService _masterService;
    private readonly ILogger<ScheduleJobBackupWorker> _logger;
    private readonly string? _backupDirectory;
    private readonly bool _coldStart;

    private const string BackupFileName = "schedule-jobs-backup.json";

    public ScheduleJobBackupWorker(
        IScheduleJobBackupNotifier notifier,
        IServiceProvider serviceProvider,
        ISlimDataStatus slimDataStatus,
        IMasterService masterService,
        ILogger<ScheduleJobBackupWorker> logger,
        IOptions<SlimDataOptions> slimDataOptions,
        IConfiguration configuration)
    {
        _notifier = notifier;
        _serviceProvider = serviceProvider;
        _slimDataStatus = slimDataStatus;
        _masterService = masterService;
        _logger = logger;

        _backupDirectory = slimDataOptions.Value.BackupDirectory;

        var coldStartValue = configuration["coldStart"];
        _coldStart = string.Equals(coldStartValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsEnabled => !string.IsNullOrWhiteSpace(_backupDirectory);

    private string BackupFilePath => Path.Combine(_backupDirectory!, BackupFileName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            _logger.LogInformation("ScheduleJobBackupWorker: disabled (SlimData:BackupDirectory is not set)");
            return;
        }

        _logger.LogInformation("ScheduleJobBackupWorker: starting, backupDir={BackupDir}, coldStart={ColdStart}",
            _backupDirectory, _coldStart);

        // Wait for the Raft cluster to be ready
        await _slimDataStatus.WaitForReadyAsync();

        // --- Restore phase (only once at startup, only on coldStart, only if master) ---
        if (_coldStart)
        {
            await TryRestoreAsync(stoppingToken);
        }

        // --- Backup phase: listen for change notifications ---
        _logger.LogInformation("ScheduleJobBackupWorker: entering backup loop");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hasSignal = await _notifier.WaitForChangeAsync(stoppingToken);
                if (!hasSignal)
                {
                    break;
                }

                // Small debounce to coalesce rapid changes
                await Task.Delay(500, stoppingToken);

                await BackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ScheduleJobBackupWorker: error during backup cycle");
                await Task.Delay(2000, stoppingToken);
            }
        }

        _logger.LogInformation("ScheduleJobBackupWorker: stopped");
    }

    private async Task TryRestoreAsync(CancellationToken ct)
    {
        try
        {
            // Wait a bit for master election to stabilize
            await Task.Delay(2000, ct);

            if (!_masterService.IsMaster)
            {
                _logger.LogInformation("ScheduleJobBackupWorker: not master, skipping restore");
                return;
            }

            if (!File.Exists(BackupFilePath))
            {
                _logger.LogInformation("ScheduleJobBackupWorker: no backup file found at {Path}, skipping restore", BackupFilePath);
                return;
            }

            // Check if the database already has ScheduleJob data (not empty)
            var provider = _serviceProvider.GetRequiredService<SlimPersistentState>();
            var supplier = (ISupplier<SlimDataPayload>)provider;
            var payload = supplier.Invoke();
            bool hasExistingScheduleData = false;
            foreach (var kv in payload.Hashsets)
            {
                if (kv.Key.StartsWith(SlimData.Endpoints.ScheduleJobPrefix, StringComparison.Ordinal) &&
                    kv.Value.Count > 0)
                {
                    hasExistingScheduleData = true;
                    break;
                }
            }

            if (hasExistingScheduleData)
            {
                _logger.LogInformation("ScheduleJobBackupWorker: database already has ScheduleJob data, skipping restore");
                return;
            }

            _logger.LogInformation("ScheduleJobBackupWorker: restoring from backup file {Path}", BackupFilePath);

            var json = await File.ReadAllTextAsync(BackupFilePath, ct);
            var backupData = JsonSerializer.Deserialize(json, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
            if (backupData?.Hashsets == null || backupData.Hashsets.Count == 0)
            {
                _logger.LogInformation("ScheduleJobBackupWorker: backup file is empty, nothing to restore");
                return;
            }

            var databaseService = _serviceProvider.GetRequiredService<IDatabaseService>();

            int restoredKeys = 0;
            foreach (var hashsetEntry in backupData.Hashsets)
            {
                var hashsetKey = hashsetEntry.Key;
                var dictionary = new Dictionary<string, byte[]>(hashsetEntry.Value.Count);
                foreach (var kv in hashsetEntry.Value)
                {
                    dictionary[kv.Key] = Convert.FromBase64String(kv.Value);
                }

                if (dictionary.Count > 0)
                {
                    await databaseService.HashSetAsync(hashsetKey, dictionary);
                    restoredKeys += dictionary.Count;
                }
            }

            _logger.LogInformation("ScheduleJobBackupWorker: restored {Count} schedule entries from backup", restoredKeys);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScheduleJobBackupWorker: error during restore");
        }
    }

    private async Task BackupAsync(CancellationToken ct)
    {
        try
        {
            var provider = _serviceProvider.GetRequiredService<SlimPersistentState>();
            var supplier = (ISupplier<SlimDataPayload>)provider;
            var payload = supplier.Invoke();

            var backupData = new ScheduleJobBackupData();

            foreach (var hashset in payload.Hashsets)
            {
                if (!hashset.Key.StartsWith(SlimData.Endpoints.ScheduleJobPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var dict = new Dictionary<string, string>(hashset.Value.Count);
                foreach (var kv in hashset.Value)
                {
                    // Skip internal TTL fields
                    if (kv.Key == SlimDataInterpreter.HashsetTtlField)
                    {
                        continue;
                    }
                    dict[kv.Key] = Convert.ToBase64String(kv.Value.ToArray());
                }

                if (dict.Count > 0)
                {
                    backupData.Hashsets[hashset.Key] = dict;
                }
            }

            if (!Directory.Exists(_backupDirectory!))
            {
                Directory.CreateDirectory(_backupDirectory!);
            }

            // Write to a temp file then atomic rename for crash safety
            var tempPath = BackupFilePath + ".tmp";
            var json = JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, BackupFilePath, overwrite: true);

            _logger.LogInformation("ScheduleJobBackupWorker: backup completed, {Count} hashsets saved to {Path}",
                backupData.Hashsets.Count, BackupFilePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScheduleJobBackupWorker: error during backup");
        }
    }
}

