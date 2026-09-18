using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNext;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.Commands;
using SlimFaas.Database;
using SlimFaas.Options;

namespace SlimFaas.Workers;

/// <summary>
/// Background worker that periodically backs up all ScheduleJob hashsets to a JSON file.
/// A SHA-256 hash of the serialized data is kept in memory to avoid writing the file
/// when nothing has changed. The backup interval is configurable via SlimData:BackupIntervalSeconds.
///
/// On startup (coldStart + empty DB), the master node restores data from the backup file.
/// On startup with existing state, every node refreshes its local backup immediately.
/// </summary>
public sealed class ScheduleJobBackupWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISlimDataStatus _slimDataStatus;
    private readonly IMasterService _masterService;
    private readonly ILogger<ScheduleJobBackupWorker> _logger;
    private readonly string? _backupDirectory;
    private readonly bool _coldStart;
    private readonly TimeSpan _backupInterval;

    private const string BackupFileName = "schedule-jobs-backup.json";
    private const string ScheduleJobPrefix = "ScheduleJob:";

    // Last hash of the serialized backup — avoids redundant file writes
    private string? _lastBackupHash;

    public ScheduleJobBackupWorker(
        IServiceProvider serviceProvider,
        ISlimDataStatus slimDataStatus,
        IMasterService masterService,
        ILogger<ScheduleJobBackupWorker> logger,
        IOptions<SlimDataOptions> slimDataOptions,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _slimDataStatus = slimDataStatus;
        _masterService = masterService;
        _logger = logger;

        _backupDirectory = slimDataOptions.Value.BackupDirectory;
        var backupIntervalSeconds = slimDataOptions.Value.BackupIntervalSeconds;
        if (backupIntervalSeconds <= 0)
        {
            _logger.LogScheduleJobBackupWorkerInvalidSlimDataBackupIntervalSecondsUsing1(backupIntervalSeconds);
            backupIntervalSeconds = 1;
        }
        _backupInterval = TimeSpan.FromSeconds(backupIntervalSeconds);

        var coldStartValue = configuration["coldStart"];
        _coldStart = string.Equals(coldStartValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsEnabled => !string.IsNullOrWhiteSpace(_backupDirectory);
    private string BackupFilePath => Path.Combine(_backupDirectory!, BackupFileName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            _logger.LogScheduleJobBackupWorkerDisabledSlimDataBackupDirectoryIsNot();
            return;
        }

        _logger.LogScheduleJobBackupWorkerStartingBackupDirIntervalColdStart(_backupDirectory, _backupInterval.TotalSeconds, _coldStart);

        await _slimDataStatus.WaitForReadyAsync();

        // --- Phase 1: Restore (coldStart + master + empty DB only) ---
        if (_coldStart)
            await TryRestoreAsync(stoppingToken);

        // --- Phase 2: Initial backup sync (every node, in case state already exists) ---
        await TryBackupAsync(stoppingToken);

        // --- Phase 3: Periodic backup loop ---
        _logger.LogScheduleJobBackupWorkerEnteringPeriodicBackupLoopInterval(_backupInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_backupInterval, stoppingToken);
                await TryBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogScheduleJobBackupWorkerUnexpectedErrorInBackupLoop(ex);
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogScheduleJobBackupWorkerStopped();
    }

    // ─── Restore ──────────────────────────────────────────────────────────────

    private async Task TryRestoreAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(2000, ct); // wait for leader election

            if (!_masterService.IsMaster)
            {
                _logger.LogScheduleJobBackupWorkerNotMasterSkippingRestore();
                return;
            }

            if (!File.Exists(BackupFilePath))
            {
                _logger.LogScheduleJobBackupWorkerNoBackupFileFoundAt(BackupFilePath);
                return;
            }

            var provider = _serviceProvider.GetRequiredService<SlimPersistentState>();
            var payload = ((ISupplier<SlimDataPayload>)provider).Invoke();

            foreach (var kv in payload.Hashsets)
            {
                if (kv.Key.StartsWith(ScheduleJobPrefix, StringComparison.Ordinal) && kv.Value.Count > 0)
                {
                    _logger.LogScheduleJobBackupWorkerDBAlreadyHasScheduleJobData();
                    return;
                }
            }

            _logger.LogScheduleJobBackupWorkerRestoringFrom(BackupFilePath);
            var json = await File.ReadAllTextAsync(BackupFilePath, ct);
            var jsonLength = json.Length;
            string jsonHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            _logger.LogScheduleJobBackupWorkerRestoreJSONMetadataLengthSha256(jsonLength, jsonHash);

            var backupData = JsonSerializer.Deserialize(json, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
            if (backupData?.Hashsets == null || backupData.Hashsets.Count == 0)
            {
                _logger.LogScheduleJobBackupWorkerBackupFileIsEmptyNothing();
                return;
            }

            var db = _serviceProvider.GetRequiredService<IDatabaseService>();
            int restoredKeys = 0;
            foreach (var entry in backupData.Hashsets)
            {
                var dict = new Dictionary<string, byte[]>(entry.Value.Count);
                foreach (var kv in entry.Value)
                    dict[kv.Key] = Convert.FromBase64String(kv.Value);

                if (dict.Count > 0)
                {
                    await db.HashSetAsync(entry.Key, dict);
                    restoredKeys += dict.Count;
                }
            }

            _logger.LogScheduleJobBackupWorkerRestoredScheduleEntries(restoredKeys);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogScheduleJobBackupWorkerErrorDuringRestore(ex);
        }
    }

    // ─── Backup ───────────────────────────────────────────────────────────────

    private async Task TryBackupAsync(CancellationToken ct)
    {
        try
        {
            var provider = _serviceProvider.GetRequiredService<SlimPersistentState>();
            var payload = ((ISupplier<SlimDataPayload>)provider).Invoke();

            // Change detection BEFORE any serialization work: incremental hash of the
            // filtered raw data (no ToArray, no base64, no JSON while nothing has
            // changed). Keys are sorted so the hash is stable regardless of the
            // dictionaries' enumeration order.
            var newHash = ComputeStateHash(payload);
            if (newHash == _lastBackupHash)
            {
                _logger.LogScheduleJobBackupWorkerNoChangeDetectedHashSkipping(newHash);
                return;
            }

            var backupData = new ScheduleJobBackupData();
            foreach (var hashset in payload.Hashsets)
            {
                if (!hashset.Key.StartsWith(ScheduleJobPrefix, StringComparison.Ordinal))
                    continue;

                var dict = new Dictionary<string, string>(hashset.Value.Count);
                foreach (var kv in hashset.Value)
                {
                    if (kv.Key == SlimDataInterpreter.HashsetTtlField)
                        continue;
                    dict[kv.Key] = Convert.ToBase64String(kv.Value.ToArray());
                }

                if (dict.Count > 0)
                    backupData.Hashsets[hashset.Key] = dict;
            }

            var json = JsonSerializer.Serialize(backupData, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

            if (!Directory.Exists(_backupDirectory!))
                Directory.CreateDirectory(_backupDirectory!);

            _logger.LogScheduleJobBackupWorkerBackupPreparedHashHashsetCountJsonLength(newHash, backupData.Hashsets.Count, json.Length);

            var tempPath = BackupFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, BackupFilePath, overwrite: true);

            _lastBackupHash = newHash;
            _logger.LogScheduleJobBackupWorkerBackupWrittenHashsetTo(backupData.Hashsets.Count, BackupFilePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogScheduleJobBackupWorkerErrorDuringBackup(ex);
        }
    }

    private static string ComputeStateHash(SlimDataPayload payload)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBuffer = new byte[sizeof(int)];

        var relevantKeys = new List<string>();
        foreach (var hashset in payload.Hashsets)
        {
            if (hashset.Key.StartsWith(ScheduleJobPrefix, StringComparison.Ordinal))
                relevantKeys.Add(hashset.Key);
        }
        relevantKeys.Sort(StringComparer.Ordinal);

        var fieldKeys = new List<string>();
        foreach (var hashsetKey in relevantKeys)
        {
            var fields = payload.Hashsets[hashsetKey];
            fieldKeys.Clear();
            foreach (var kv in fields)
            {
                if (kv.Key != SlimDataInterpreter.HashsetTtlField)
                    fieldKeys.Add(kv.Key);
            }

            if (fieldKeys.Count == 0)
                continue;

            fieldKeys.Sort(StringComparer.Ordinal);
            AppendChunk(hasher, Encoding.UTF8.GetBytes(hashsetKey), lengthBuffer);
            foreach (var fieldKey in fieldKeys)
            {
                AppendChunk(hasher, Encoding.UTF8.GetBytes(fieldKey), lengthBuffer);
                AppendChunk(hasher, fields[fieldKey].Span, lengthBuffer);
            }
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static void AppendChunk(IncrementalHash hasher, ReadOnlySpan<byte> data, byte[] lengthBuffer)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, data.Length);
        hasher.AppendData(lengthBuffer);
        hasher.AppendData(data);
    }
}

