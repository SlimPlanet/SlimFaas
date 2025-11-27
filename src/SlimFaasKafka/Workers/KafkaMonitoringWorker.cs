using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prometheus;
using SlimFaasKafka.Config;
using SlimFaasKafka.Kafka;
using SlimFaasKafka.Services;

namespace SlimFaasKafka.Workers;

/// <summary>
/// Worker qui surveille le lag de différents couples (topic, consumer group)
/// et déclenche un wake up SlimFaas si nécessaire.
/// En plus du lag, il surveille l'activité récente (offsets consommés)
/// pour garder les pods "allumés".
/// </summary>
public sealed class KafkaMonitoringWorker : BackgroundService
{
    private readonly ILogger<KafkaMonitoringWorker> _logger;
    private readonly IOptionsMonitor<KafkaOptions> _kafkaOptionsMonitor;
    private readonly IOptionsMonitor<BindingsOptions> _bindingsOptionsMonitor;
    private readonly ISlimFaasClient _slimFaasClient;
    private readonly IKafkaLagProvider _kafkaLagProvider;

    // Pour éviter de spammer SlimFaas : garde en mémoire le dernier wake.
    // Clé au niveau (Topic, Group, Function) pour permettre plusieurs fonctions
    // sur le même couple topic/group avec des cooldowns indépendants.
    private readonly ConcurrentDictionary<(string Topic, string Group, string Function), DateTimeOffset> _lastWakeUp = new();

    // Offset total commité par group (pour détecter l'activité récente).
    // Clé au niveau (Topic, Group) car l'activité est propre au consumer group.
    private readonly ConcurrentDictionary<(string Topic, string Group), long> _lastCommittedOffsets = new();

    // Dernière activité observée (offset qui avance) par group.
    private readonly ConcurrentDictionary<(string Topic, string Group), DateTimeOffset> _lastActivity = new();

    // --- Prometheus metrics ---
    //
    // Toutes les métriques sont préfixées par "slimfaaskafka_"
    // et labellisées par topic, group et function.

    private static readonly Counter WakeupCounter = Metrics.CreateCounter(
        "slimfaaskafka_wakeups_total",
        "Number of wake-ups triggered by SlimFaasKafka.",
        new CounterConfiguration
        {
            LabelNames = new[] { "topic", "group", "function", "reason" }
        });

    private static readonly Gauge PendingMessagesGauge = Metrics.CreateGauge(
        "slimfaaskafka_pending_messages",
        "Last observed number of pending messages per topic/group/function.",
        new GaugeConfiguration
        {
            LabelNames = new[] { "topic", "group", "function" }
        });

    private static readonly Gauge LastActivityGauge = Metrics.CreateGauge(
        "slimfaaskafka_last_activity_timestamp_seconds",
        "Last observed activity time (offset advancing) as Unix time in seconds.",
        new GaugeConfiguration
        {
            LabelNames = new[] { "topic", "group", "function" }
        });

    public KafkaMonitoringWorker(
        ILogger<KafkaMonitoringWorker> logger,
        IOptionsMonitor<KafkaOptions> kafkaOptionsMonitor,
        IOptionsMonitor<BindingsOptions> bindingsOptionsMonitor,
        ISlimFaasClient slimFaasClient,
        IKafkaLagProvider kafkaLagProvider)
    {
        _logger = logger;
        _kafkaOptionsMonitor = kafkaOptionsMonitor;
        _bindingsOptionsMonitor = bindingsOptionsMonitor;
        _slimFaasClient = slimFaasClient;
        _kafkaLagProvider = kafkaLagProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kafkaOptionsAtStart = _kafkaOptionsMonitor.CurrentValue;
        _logger.LogInformation(
            "KafkaMonitoringWorker started. BootstrapServers = {BootstrapServers}, ClientId = {ClientId}",
            kafkaOptionsAtStart.BootstrapServers,
            kafkaOptionsAtStart.ClientId);

        while (!stoppingToken.IsCancellationRequested)
        {
            var kafkaOptions = _kafkaOptionsMonitor.CurrentValue;
            var bindingsOptions = _bindingsOptionsMonitor.CurrentValue;

            if (bindingsOptions.Bindings.Count == 0)
            {
                _logger.LogDebug("No bindings configured, skipping check");
            }
            else
            {
                try
                {
                    await CheckBindingsAsync(kafkaOptions, bindingsOptions, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // normal lors de l'arrêt
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error while checking Kafka bindings");
                }
            }

            var delaySeconds = Math.Max(1, kafkaOptions.CheckIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore lors de l'arrêt
            }
        }

        _logger.LogInformation("KafkaMonitoringWorker stopped");
    }

    // NOTE: reste private, on l'appellera en test via réflexion.
    private async Task CheckBindingsAsync(
        KafkaOptions kafkaOptions,
        BindingsOptions bindingsOptions,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var binding in bindingsOptions.Bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var groupKey = (binding.Topic, binding.ConsumerGroupId);
            var wakeKey = (binding.Topic, binding.ConsumerGroupId, binding.FunctionName);

            try
            {
                var (pending, committedSum, usedAdmin) =
                    await _kafkaLagProvider.GetLagAsync(binding, kafkaOptions, cancellationToken)
                        .ConfigureAwait(false);

                if (!usedAdmin)
                {
                    _logger.LogInformation(
                        "Binding topic={Topic}, group={Group}, function={Function} is using consumer-only heuristic " +
                        "(no visibility on consumer group offsets: recent-consumption keep-alive is limited).",
                        binding.Topic,
                        binding.ConsumerGroupId,
                        binding.FunctionName);
                }

                PendingMessagesGauge
                    .WithLabels(binding.Topic, binding.ConsumerGroupId, binding.FunctionName)
                    .Set(pending);

                bool recentActivity = false;
                long consumedDelta = 0;

                if (committedSum > 0 && usedAdmin)
                {
                    _lastCommittedOffsets.AddOrUpdate(
                        groupKey,
                        addValueFactory: _ =>
                        {
                            var initial = committedSum;
                            _lastActivity[groupKey] = now;
                            LastActivityGauge
                                .WithLabels(binding.Topic, binding.ConsumerGroupId, binding.FunctionName)
                                .Set(now.ToUnixTimeSeconds());
                            return initial;
                        },
                        updateValueFactory: (_, oldCommitted) =>
                        {
                            var delta = committedSum - oldCommitted;
                            if (delta > 0)
                            {
                                consumedDelta = delta;
                                _lastActivity[groupKey] = now;
                                LastActivityGauge
                                    .WithLabels(binding.Topic, binding.ConsumerGroupId, binding.FunctionName)
                                    .Set(now.ToUnixTimeSeconds());
                            }

                            return committedSum > oldCommitted ? committedSum : oldCommitted;
                        });

                    if (consumedDelta >= binding.MinConsumedDeltaForActivity &&
                        binding.ActivityKeepAliveSeconds > 0)
                    {
                        recentActivity = true;
                    }
                }

                if (!recentActivity && binding.ActivityKeepAliveSeconds > 0 &&
                    _lastActivity.TryGetValue(groupKey, out var lastAct))
                {
                    var keepAliveWindow = TimeSpan.FromSeconds(Math.Max(1, binding.ActivityKeepAliveSeconds));
                    if (now - lastAct <= keepAliveWindow)
                    {
                        recentActivity = true;
                    }
                }

                _logger.LogDebug(
                    "Binding topic={Topic}, group={Group}, function={Function} has {Pending} pending messages, consumedDelta={ConsumedDelta}, recentActivity={RecentActivity}, usedAdmin={UsedAdmin}",
                    binding.Topic,
                    binding.ConsumerGroupId,
                    binding.FunctionName,
                    pending,
                    consumedDelta,
                    recentActivity,
                    usedAdmin);

                var shouldWakeForPending = pending >= binding.MinPendingMessages;
                var shouldWakeForActivity = recentActivity && binding.ActivityKeepAliveSeconds > 0;

                if (shouldWakeForPending || shouldWakeForActivity)
                {
                    if (IsOutOfCooldown(wakeKey, binding))
                    {
                        var reason = shouldWakeForPending && shouldWakeForActivity
                            ? "pending_and_activity"
                            : shouldWakeForPending
                                ? "pending"
                                : "activity";

                        _logger.LogInformation(
                            "Triggering wake up for function {Function} (pending={Pending}, recentActivity={RecentActivity}, reason={Reason}, usedAdmin={UsedAdmin}) on topic={Topic}, group={Group}",
                            binding.FunctionName,
                            pending,
                            recentActivity,
                            reason,
                            usedAdmin,
                            binding.Topic,
                            binding.ConsumerGroupId);

                        await _slimFaasClient.WakeAsync(binding.FunctionName, cancellationToken);
                        _lastWakeUp[wakeKey] = now;

                        WakeupCounter
                            .WithLabels(binding.Topic, binding.ConsumerGroupId, binding.FunctionName, reason)
                            .Inc();
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Cooldown still active for topic={Topic}, group={Group}, function={Function}",
                            binding.Topic,
                            binding.ConsumerGroupId,
                            binding.FunctionName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error while checking binding topic={Topic}, group={Group}",
                    binding.Topic,
                    binding.ConsumerGroupId);
            }
        }
    }

    private bool IsOutOfCooldown((string Topic, string Group, string Function) key, TopicBinding binding)
    {
        if (!_lastWakeUp.TryGetValue(key, out var last))
        {
            return true;
        }

        var cooldown = TimeSpan.FromSeconds(Math.Max(0, binding.CooldownSeconds));
        return DateTimeOffset.UtcNow - last >= cooldown;
    }
}
