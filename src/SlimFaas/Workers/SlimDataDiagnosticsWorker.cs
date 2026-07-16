using System.Diagnostics;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Workers;

public sealed class SlimDataDiagnosticsWorker(
    SlimPersistentState persistentState,
    IRaftCluster cluster,
    IDatabaseService databaseService,
    DynamicGaugeService gauges,
    ILogger<SlimDataDiagnosticsWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

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
        if (cluster.AuditTrail is WriteAheadLog wal)
            gauges.SetGaugeValue("slimdata_raft_applied_log_index", wal.LastAppliedIndex,
                "Last applied Raft WAL entry index");

        var hasConsensus = !cluster.ConsensusToken.IsCancellationRequested;
        var hasLease = cluster.TryGetLeaseToken(out var leaseToken) && !leaseToken.IsCancellationRequested;
        gauges.SetGaugeValue("slimdata_raft_has_consensus", hasConsensus ? 1 : 0,
            "Whether this node currently sees Raft consensus");
        gauges.SetGaugeValue("slimdata_raft_has_leader_lease", hasLease ? 1 : 0,
            "Whether this node owns a valid Raft leader lease");
        gauges.SetGaugeValue("slimdata_raft_member_count", cluster.Members.Count,
            "Number of remote members in the local Raft configuration");

        if (databaseService is SlimDataService service)
        {
            foreach (var queue in service.BatchQueueStatistics)
            {
                var labels = new Dictionary<string, string> { ["kind"] = queue.Kind };
                gauges.SetGaugeValue("slimdata_batch_queue_items", queue.Items,
                    "Number of items waiting in an adaptive batch queue", labels);
                gauges.SetGaugeValue("slimdata_batch_queue_bytes", queue.Bytes,
                    "Estimated bytes waiting in an adaptive batch queue", labels);
            }
        }

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
