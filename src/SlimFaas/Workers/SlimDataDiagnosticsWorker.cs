using System.Diagnostics;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Workers;

public sealed class SlimDataDiagnosticsWorker(
    SlimPersistentState persistentState,
    IRaftCluster cluster,
    SlimDataCommandBatchCoordinator commandBatchCoordinator,
    RaftAppendEntriesCommitIndexGuard appendEntriesCommitIndexGuard,
    ISlimDataProtocolCompatibility protocolCompatibility,
    IDatabaseService databaseService,
    DynamicGaugeService gauges,
    ILogger<SlimDataDiagnosticsWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly (SlimDataSnapshotTrigger Trigger, IReadOnlyDictionary<string, string> Labels)[]
        SnapshotTriggerMetrics =
        [
            (SlimDataSnapshotTrigger.Entries, new Dictionary<string, string> { ["cause"] = "entries" }),
            (SlimDataSnapshotTrigger.Bytes, new Dictionary<string, string> { ["cause"] = "bytes" }),
            (SlimDataSnapshotTrigger.Incompatible, new Dictionary<string, string> { ["cause"] = "incompatible" })
        ];
    private static readonly IReadOnlyDictionary<string, string> WalRecoveryLabels =
        new Dictionary<string, string> { ["mode"] = "wal" };
    private static readonly IReadOnlyDictionary<string, string> SnapshotRecoveryLabels =
        new Dictionary<string, string> { ["mode"] = "snapshot" };
    private long _previousLastLogIndex = -1;
    private long _previousAppliedLogIndex = -1;
    private long _previousSampleTimestamp;
    private long _recoveryStartedTimestamp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RecordMetrics();
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to record SlimData diagnostics");
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void RecordMetrics()
    {
        using var process = Process.GetCurrentProcess();
        gauges.SetGaugeValue("slimdata_process_working_set_bytes", process.WorkingSet64,
            "SlimData process working set in bytes");
        gauges.SetGaugeValue("slimdata_process_private_memory_bytes", process.PrivateMemorySize64,
            "SlimData process private memory in bytes");
        gauges.SetGaugeValue("slimdata_process_managed_heap_bytes", GC.GetTotalMemory(forceFullCollection: false),
            "SlimData managed heap in bytes");
        gauges.SetGaugeValue(
            "slimdata_process_cpu_seconds_total",
            process.TotalProcessorTime.TotalSeconds,
            "Total CPU time consumed by the SlimData process in seconds");

        var state = persistentState.GetStateMetrics();
        gauges.SetGaugeValue("slimdata_state_payload_bytes", state.PayloadBytes,
            "Bytes retained by values in the SlimData state");
        gauges.SetGaugeValue("slimdata_state_key_values", state.KeyValues,
            "Number of key/value entries in SlimData");
        gauges.SetGaugeValue("slimdata_state_hashset_items", state.HashsetItems,
            "Number of hashset items in SlimData");
        gauges.SetGaugeValue("slimdata_state_queue_items", state.QueueItems,
            "Number of queue items in SlimData");

        gauges.SetGaugeValue("slimdata_snapshot_in_progress", persistentState.IsSnapshotting ? 1 : 0,
            "Whether a SlimData snapshot is being persisted");
        gauges.SetGaugeValue("slimdata_snapshot_restore_in_progress", persistentState.IsRestoring ? 1 : 0,
            "Whether a SlimData snapshot is being restored");
        gauges.SetGaugeValue("slimdata_snapshot_last_size_bytes", persistentState.LastSnapshotSizeBytes,
            "Size in bytes of the latest SlimData snapshot or its payload");
        gauges.SetGaugeValue("slimdata_snapshot_last_duration_milliseconds", persistentState.LastSnapshotDurationMilliseconds,
            "Duration of the latest SlimData snapshot operation");
        gauges.SetGaugeValue("slimdata_wal_bytes_since_snapshot", persistentState.WalBytesSinceSnapshot,
            "Bytes of applied SlimData WAL entries accumulated since the latest snapshot request or restore");

        foreach (var (trigger, labels) in SnapshotTriggerMetrics)
        {
            gauges.SetGaugeValue(
                "slimdata_snapshot_last_trigger",
                persistentState.LastSnapshotTrigger == trigger ? 1 : 0,
                "Cause of the latest SlimData snapshot request in this process",
                labels);
        }

        foreach (var skipped in persistentState.GetSkippedCommandMetrics())
        {
            var labels = new Dictionary<string, string>
            {
                ["command_id"] = skipped.CommandId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            gauges.SetGaugeValue("slimdata_raft_skipped_command_total", skipped.Count,
                "Number of incompatible Raft commands skipped by command ID", labels);
        }

        var safety = persistentState.GetRaftSafetyMetrics();
        gauges.SetGaugeValue("slimdata_raft_command_size_violation_total", safety.SizeViolations,
            "Number of skipped Raft commands violating a size limit");
        gauges.SetGaugeValue("slimdata_raft_command_format_violation_total", safety.FormatViolations,
            "Number of skipped Raft commands violating the binary format");
        gauges.SetGaugeValue("slimdata_raft_last_skipped_index", safety.LastSkippedIndex,
            "Raft index of the latest skipped command, or -1 when none was skipped");
        gauges.SetGaugeValue("slimdata_raft_last_skipped_term", safety.LastSkippedTerm,
            "Raft term of the latest skipped command, or -1 when none was skipped");
        gauges.SetGaugeValue("slimdata_raft_last_skipped_command_id", safety.LastSkippedCommandId,
            "Command ID of the latest skipped command, or -1 when unavailable");
        gauges.SetGaugeValue("slimdata_raft_last_skipped_length_bytes", safety.LastSkippedLength,
            "Declared length of the latest skipped command, or -1 when unavailable");

        gauges.SetGaugeValue("slimdata_raft_last_log_index", cluster.AuditTrail.LastEntryIndex,
            "Last Raft WAL entry index");
        gauges.SetGaugeValue("slimdata_raft_committed_log_index", cluster.AuditTrail.LastCommittedEntryIndex,
            "Last committed Raft WAL entry index");
        gauges.SetGaugeValue(
            "slimdata_raft_commit_index_clamped_total",
            appendEntriesCommitIndexGuard.ClampedRequests,
            "Number of incoming Raft AppendEntries requests whose commit index was bounded to the last transmitted entry");
        var lastLogIndex = cluster.AuditTrail.LastEntryIndex;
        var hasAppliedLogIndex = cluster.AuditTrail is WriteAheadLog;
        var appliedLogIndex = hasAppliedLogIndex
            ? ((WriteAheadLog)cluster.AuditTrail).LastAppliedIndex
            : lastLogIndex;
        gauges.SetGaugeValue("slimdata_raft_applied_log_index", appliedLogIndex,
            "Last applied Raft WAL entry index");
        gauges.SetGaugeValue("slimdata_raft_replication_lag", Math.Max(0, lastLogIndex - appliedLogIndex),
            "Number of local Raft WAL entries not yet applied");

        var now = Stopwatch.GetTimestamp();
        if (_previousSampleTimestamp != 0)
        {
            var elapsedSeconds = (now - _previousSampleTimestamp) / (double)Stopwatch.Frequency;
            if (elapsedSeconds > 0)
            {
                var walGenerationRate = Math.Max(0, lastLogIndex - _previousLastLogIndex) / elapsedSeconds;
                var catchUpRate = hasAppliedLogIndex
                    ? Math.Max(0, appliedLogIndex - _previousAppliedLogIndex) / elapsedSeconds
                    : 0;
                gauges.SetGaugeValue("slimdata_raft_wal_generation_rate", walGenerationRate,
                    "Raft WAL entries generated per second");
                gauges.SetGaugeValue("slimdata_raft_catch_up_rate", catchUpRate,
                    "Raft WAL entries applied per second");
                gauges.SetGaugeValue(
                    "slimdata_raft_catch_up_cannot_converge",
                    hasAppliedLogIndex && appliedLogIndex < lastLogIndex && walGenerationRate > catchUpRate ? 1 : 0,
                    "Whether local Raft catch-up is currently slower than WAL generation");
            }
        }
        else
        {
            gauges.SetGaugeValue("slimdata_raft_wal_generation_rate", 0,
                "Raft WAL entries generated per second");
            gauges.SetGaugeValue("slimdata_raft_catch_up_rate", 0,
                "Raft WAL entries applied per second");
            gauges.SetGaugeValue("slimdata_raft_catch_up_cannot_converge", 0,
                "Whether local Raft catch-up is currently slower than WAL generation");
        }

        _previousLastLogIndex = lastLogIndex;
        _previousAppliedLogIndex = appliedLogIndex;
        _previousSampleTimestamp = now;

        var recovering = hasAppliedLogIndex && appliedLogIndex < lastLogIndex;
        if (recovering && _recoveryStartedTimestamp == 0)
            _recoveryStartedTimestamp = now;
        else if (!recovering)
            _recoveryStartedTimestamp = 0;

        var snapshotRecovery = persistentState.IsRestoring || persistentState.IsSnapshotting;
        gauges.SetGaugeValue("slimdata_raft_recovery_mode",
            recovering && !snapshotRecovery ? 1 : 0,
            "Current SlimData Raft recovery mode", WalRecoveryLabels);
        gauges.SetGaugeValue("slimdata_raft_recovery_mode",
            recovering && snapshotRecovery ? 1 : 0,
            "Current SlimData Raft recovery mode", SnapshotRecoveryLabels);

        gauges.SetGaugeValue(
            "slimdata_raft_recovery_duration_seconds",
            _recoveryStartedTimestamp == 0
                ? 0
                : (now - _recoveryStartedTimestamp) / (double)Stopwatch.Frequency,
            "Duration of the current SlimData Raft recovery");

        var hasConsensus = !cluster.ConsensusToken.IsCancellationRequested;
        var hasLease = cluster.TryGetLeaseToken(out var leaseToken) && !leaseToken.IsCancellationRequested;
        gauges.SetGaugeValue("slimdata_raft_has_consensus", hasConsensus ? 1 : 0,
            "Whether this node currently sees Raft consensus");
        gauges.SetGaugeValue("slimdata_raft_has_leader_lease", hasLease ? 1 : 0,
            "Whether this node owns a valid Raft leader lease");
        gauges.SetGaugeValue("slimdata_raft_protocol_compatible", protocolCompatibility.IsCompatible ? 1 : 0,
            "Whether the local node and current Raft leader use the same SlimData command protocol");
        gauges.SetGaugeValue("slimdata_raft_member_count", cluster.Members.Count,
            "Number of remote members in the local Raft configuration");

        if (databaseService is SlimDataService service)
        {
            var batchModeLabels = new Dictionary<string, string>
            {
                ["mode"] = service.BatchMode.ToString()
            };
            gauges.SetGaugeValue("slimdata_batch_mode", 1,
                "Configured local SlimData batch queue topology", batchModeLabels);
            gauges.SetGaugeValue("slimdata_batch_partition_count", service.BatchPartitionCount,
                "Number of local SlimData mutation batch queues");
            gauges.SetGaugeValue(
                "slimdata_batch_low_load_fast_path_enabled",
                service.LowLoadFastPathEnabled ? 1 : 0,
                "Whether zero-delay local batching is enabled under low load");
            gauges.SetGaugeValue(
                "slimdata_batch_queue_low_latency_enabled",
                service.QueueLowLatencyEnabled ? 1 : 0,
                "Whether queue-critical mutations can interrupt the local adaptive batching cooldown");
            gauges.SetGaugeValue(
                "slimdata_batch_queue_max_wait_milliseconds",
                service.QueueMutationMaxWait.TotalMilliseconds,
                "Configured maximum local wait for queue-critical mutations");

            var load = service.BatchLoadSnapshot;
            gauges.SetGaugeValue("slimdata_batch_local_requests_per_second", load.RequestsPerSecond,
                "Local mutation request rate measured over a rolling five-second window");
            gauges.SetGaugeValue(
                "slimdata_batch_low_load_fast_path_active",
                service.LowLoadFastPathEnabled && load.Level == SlimDataBatchLoadLevel.Low ? 1 : 0,
                "Whether zero-delay local batching is currently active");
            gauges.SetGaugeValue(
                "slimdata_batch_local_delay_milliseconds",
                load.Timing.Delay.TotalMilliseconds,
                "Current local cooldown between mutation batches");
            gauges.SetGaugeValue(
                "slimdata_batch_local_coalesce_milliseconds",
                load.Timing.CoalesceWindow.TotalMilliseconds,
                "Current local mutation coalescence window");

            foreach (var queue in service.BatchQueueStatistics)
            {
                var labels = new Dictionary<string, string> { ["kind"] = queue.Kind };
                gauges.SetGaugeValue("slimdata_batch_queue_items", queue.Items,
                    "Number of items waiting in an adaptive batch queue", labels);
                gauges.SetGaugeValue("slimdata_batch_queue_bytes", queue.Bytes,
                    "Estimated bytes waiting in an adaptive batch queue", labels);
                gauges.SetGaugeValue("slimdata_batch_latency_sensitive_operations_total",
                    queue.LatencySensitiveOperations,
                    "Queue-critical operations submitted to an adaptive batch queue", labels);
                gauges.SetGaugeValue("slimdata_batch_forced_flush_total", queue.ForcedFlushes,
                    "Adaptive batches flushed because a queue-critical deadline expired", labels);
                gauges.SetGaugeValue("slimdata_batch_last_latency_sensitive_wait_milliseconds",
                    queue.LastLatencySensitiveWaitMilliseconds,
                    "Local batch wait of the latest queue-critical operation", labels);
            }
        }

        var commandBatch = commandBatchCoordinator.GetStatistics();
        gauges.SetGaugeValue("slimdata_command_batch_queue_requests", commandBatch.QueueRequests,
            "Number of producer batches waiting in the centralized leader queue");
        gauges.SetGaugeValue("slimdata_command_batch_queue_bytes", commandBatch.QueueBytes,
            "Estimated producer batch bytes waiting in the centralized leader queue");
        gauges.SetGaugeValue("slimdata_command_batch_raft_total", commandBatch.RaftBatches,
            "Number of composite RAFT entries produced by the centralized batch coordinator");
        gauges.SetGaugeValue("slimdata_command_batch_producer_total", commandBatch.ProducerBatches,
            "Number of producer batches included in composite RAFT entries");
        gauges.SetGaugeValue("slimdata_command_batch_operations_total", commandBatch.Operations,
            "Number of mutations included in composite RAFT entries");
        gauges.SetGaugeValue("slimdata_command_batch_duplicates_total", commandBatch.Duplicates,
            "Number of duplicate producer batches served from durable deduplication state");
        gauges.SetGaugeValue("slimdata_command_batch_sequence_gaps_total", commandBatch.SequenceGaps,
            "Number of rejected producer sequence gaps");
        gauges.SetGaugeValue("slimdata_command_batch_last_raft_latency_milliseconds",
            commandBatch.LastRaftLatencyMilliseconds,
            "Commit and local apply latency of the latest composite RAFT batch");
        gauges.SetGaugeValue("slimdata_command_batch_last_max_queue_wait_milliseconds",
            commandBatch.LastMaxQueueWaitMilliseconds,
            "Maximum leader queue wait of a producer request in the latest composite RAFT batch");
        gauges.SetGaugeValue("slimdata_command_batch_last_producer_batches",
            commandBatch.LastProducerBatchCount,
            "Producer batch count in the latest composite RAFT entry");
        gauges.SetGaugeValue("slimdata_command_batch_last_operations",
            commandBatch.LastOperationCount,
            "Mutation count in the latest composite RAFT entry");
        gauges.SetGaugeValue("slimdata_command_batch_last_payload_bytes",
            commandBatch.LastPayloadBytes,
            "Estimated HTTP payload bytes included in the latest composite RAFT entry");

        RecordCgroupMemory();
    }

    private void RecordCgroupMemory()
    {
        const string currentPath = "/sys/fs/cgroup/memory.current";
        if (File.Exists(currentPath) && long.TryParse(File.ReadAllText(currentPath).Trim(), out var current))
            gauges.SetGaugeValue("slimdata_cgroup_memory_current_bytes", current,
                "Current cgroup memory usage in bytes");

        const string statisticsPath = "/sys/fs/cgroup/memory.stat";
        if (!File.Exists(statisticsPath))
            return;

        foreach (var line in File.ReadLines(statisticsPath))
        {
            var separator = line.IndexOf(' ');
            if (separator <= 0 || !long.TryParse(line.AsSpan(separator + 1), out var value))
                continue;

            var name = line[..separator];
            if (name is "anon" or "file" or "shmem")
            {
                gauges.SetGaugeValue($"slimdata_cgroup_memory_{name}_bytes", value,
                    $"Cgroup {name} memory in bytes");
            }
        }
    }
}
