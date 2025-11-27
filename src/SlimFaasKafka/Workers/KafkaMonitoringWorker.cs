using System.Collections.Concurrent;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;
using Prometheus;
using SlimFaasKafka.Config;
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

    // Pour éviter de spammer SlimFaas : garde en mémoire le dernier wake pour chaque binding.
    private readonly ConcurrentDictionary<(string Topic, string Group), DateTimeOffset> _lastWakeUp = new();

    // Offset total commité par binding (pour détecter l'activité récente).
    private readonly ConcurrentDictionary<(string Topic, string Group), long> _lastCommittedOffsets = new();

    // Dernière activité observée (offset qui avance) par binding.
    private readonly ConcurrentDictionary<(string Topic, string Group), DateTimeOffset> _lastActivity = new();

    // --- Prometheus metrics ---

    private static readonly Counter WakeupCounter = Metrics.CreateCounter(
        "slimkafka_wakeups_total",
        "Number of wake-ups triggered by SlimKafka.",
        new CounterConfiguration
        {
            LabelNames = new[] { "topic", "group", "function", "reason" }
        });

    private static readonly Gauge PendingMessagesGauge = Metrics.CreateGauge(
        "slimkafka_pending_messages",
        "Last observed number of pending messages per topic/group/function.",
        new GaugeConfiguration
        {
            LabelNames = new[] { "topic", "group", "function" }
        });

    private static readonly Gauge LastActivityGauge = Metrics.CreateGauge(
        "slimkafka_last_activity_timestamp_seconds",
        "Last observed activity time (offset advancing) as Unix time in seconds.",
        new GaugeConfiguration
        {
            LabelNames = new[] { "topic", "group", "function" }
        });

    public KafkaMonitoringWorker(
        ILogger<KafkaMonitoringWorker> logger,
        IOptionsMonitor<KafkaOptions> kafkaOptionsMonitor,
        IOptionsMonitor<BindingsOptions> bindingsOptionsMonitor,
        ISlimFaasClient slimFaasClient)
    {
        _logger = logger;
        _kafkaOptionsMonitor = kafkaOptionsMonitor;
        _bindingsOptionsMonitor = bindingsOptionsMonitor;
        _slimFaasClient = slimFaasClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kafkaOptions = _kafkaOptionsMonitor.CurrentValue;
        _logger.LogInformation("KafkaMonitoringWorker started. BootstrapServers = {BootstrapServers}, ClientId = {ClientId}",
            kafkaOptions.BootstrapServers, kafkaOptions.ClientId);

        while (!stoppingToken.IsCancellationRequested)
        {
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

    private async Task CheckBindingsAsync(
        KafkaOptions kafkaOptions,
        BindingsOptions bindingsOptions,
        CancellationToken cancellationToken)
    {
        var adminConfig = new AdminClientConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers,
            ClientId = kafkaOptions.ClientId
        };

        // Ce consumer ne rejoint pas le groupe surveillé, il sert uniquement
        // à récupérer les watermarks (high offsets).
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers,
            GroupId = "slimkafka-watermark",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = kafkaOptions.AllowAutoCreateTopics
        };

        using var adminClient = new AdminClientBuilder(adminConfig).Build();
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(consumerConfig).Build();

        var timeout = TimeSpan.FromSeconds(Math.Max(1, kafkaOptions.KafkaTimeoutSeconds));
        var now = DateTimeOffset.UtcNow;

        foreach (var binding in bindingsOptions.Bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = (binding.Topic, binding.ConsumerGroupId);

            try
            {
                // --- Récupération pending + offsets via AdminClient si possible, sinon fallback ---
                var (pending, committedSum, usedAdmin) =
                    await GetPendingWithAdminOrFallbackAsync(
                        adminClient,
                        consumer,
                        binding,
                        timeout,
                        cancellationToken)
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

                // Met à jour la gauge des pending
                PendingMessagesGauge
                    .WithLabels(binding.Topic, binding.ConsumerGroupId, binding.FunctionName)
                    .Set(pending);

                // --- Gestion de l'activité récente (offsets qui avancent) ---
                bool recentActivity = false;
                long consumedDelta = 0;

                if (committedSum > 0 && usedAdmin)
                {
                    _lastCommittedOffsets.AddOrUpdate(
                        key,
                        addValueFactory: _ =>
                        {
                            // Première fois qu'on voit ce binding : on initialise sans considérer de delta
                            var initial = committedSum;
                            _lastActivity[key] = now;
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
                                _lastActivity[key] = now;
                                LastActivityGauge
                                    .WithLabels(binding.Topic, binding.ConsumerGroupId, binding.FunctionName)
                                    .Set(now.ToUnixTimeSeconds());
                            }

                            return committedSum > oldCommitted ? committedSum : oldCommitted;
                        });

                    // Si on a effectivement détecté un delta strictement positif
                    // et qu'il dépasse le seuil MinConsumedDeltaForActivity,
                    // on considère que l'activité est valable pour le keep-alive.
                    if (consumedDelta >= binding.MinConsumedDeltaForActivity &&
                        binding.ActivityKeepAliveSeconds > 0)
                    {
                        recentActivity = true;
                    }
                }

                // Même si pas de nouveau delta sur ce cycle, on peut encore être dans la fenêtre de keep-alive
                if (!recentActivity && binding.ActivityKeepAliveSeconds > 0 &&
                    _lastActivity.TryGetValue(key, out var lastAct))
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

                // --- Décision de wake up ---
                var shouldWakeForPending = pending >= binding.MinPendingMessages;
                var shouldWakeForActivity = recentActivity && binding.ActivityKeepAliveSeconds > 0;

                if (shouldWakeForPending || shouldWakeForActivity)
                {
                    if (IsOutOfCooldown(key, binding))
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
                        _lastWakeUp[key] = now;

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
            catch (KafkaException kex)
            {
                _logger.LogWarning(
                    kex,
                    "Kafka error while checking binding topic={Topic}, group={Group}",
                    binding.Topic,
                    binding.ConsumerGroupId);
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

    /// <summary>
    /// Essaie d'abord d'utiliser l'AdminClient (lag réel par consumer group).
    /// Si non autorisé ou erreur, bascule sur une heuristique côté consumer uniquement,
    /// avec des logs explicites.
    /// </summary>
    private async Task<(long pending, long totalCommitted, bool usedAdmin)> GetPendingWithAdminOrFallbackAsync(
        IAdminClient adminClient,
        IConsumer<Ignore, Ignore> consumer,
        TopicBinding binding,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var (pending, totalCommitted) = await GetPendingUsingAdminAsync(
                    adminClient,
                    consumer,
                    binding,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            return (pending, totalCommitted, usedAdmin: true);
        }
        catch (ListConsumerGroupOffsetsException ex)
        {
            _logger.LogWarning(
                ex,
                "AdminClient.ListConsumerGroupOffsetsAsync failed for group '{GroupId}', topic '{Topic}'. " +
                "Falling back to consumer-only heuristic (no visibility on consumer group offsets).",
                binding.ConsumerGroupId,
                binding.Topic);
        }
        catch (KafkaException ex) when (
            ex.Error.Code == ErrorCode.GroupAuthorizationFailed ||
            ex.Error.Code == ErrorCode.TopicAuthorizationFailed)
        {
            _logger.LogWarning(
                ex,
                "Not authorized to read consumer group offsets for group '{GroupId}', topic '{Topic}'. " +
                "Falling back to consumer-only heuristic (no visibility on consumer group offsets).",
                binding.ConsumerGroupId,
                binding.Topic);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unexpected error while using AdminClient for group '{GroupId}', topic '{Topic}'. " +
                "Falling back to consumer-only heuristic.",
                binding.ConsumerGroupId,
                binding.Topic);
        }

        var fallback = GetPendingUsingConsumerOnly(adminClient, consumer, binding, timeout);
        return (fallback.pending, fallback.totalCommitted, usedAdmin: false);
    }

    /// <summary>
    /// Mode "idéal" : utilise l'AdminClient pour récupérer les offsets du consumer group cible
    /// et calcule le lag réel par partition (high watermark - committed).
    /// </summary>
    private static async Task<(long pending, long totalCommitted)> GetPendingUsingAdminAsync(
        IAdminClient adminClient,
        IConsumer<Ignore, Ignore> consumer,
        TopicBinding binding,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long pending = 0;
        long totalCommitted = 0;

        // 1. Récupère les partitions du topic via l’AdminClient
        var metadata = adminClient.GetMetadata(binding.Topic, timeout);
        var topicMetadata = metadata.Topics.SingleOrDefault(t => t.Topic == binding.Topic);

        if (topicMetadata == null || topicMetadata.Error.IsError)
        {
            // Topic inconnu ou inaccessible
            return (0, 0);
        }

        var topicPartitions = topicMetadata.Partitions
            .Select(p => new TopicPartition(binding.Topic, p.PartitionId))
            .ToList();

        if (topicPartitions.Count == 0)
        {
            return (0, 0);
        }

        // 2. Demande des offsets du consumer group ciblé
        var groupPartitions = new[]
        {
            new ConsumerGroupTopicPartitions(binding.ConsumerGroupId, topicPartitions)
        };

        var options = new ListConsumerGroupOffsetsOptions
        {
            RequestTimeout = timeout
        };

        var results = await adminClient
            .ListConsumerGroupOffsetsAsync(groupPartitions, options)
            .ConfigureAwait(false);

        var groupResult = results.Single(); // on a passé exactement 1 group

        // Map TopicPartition -> info offset+erreur
        var byPartition = groupResult.Partitions
            .ToDictionary(
                p => p.TopicPartition,
                p => p,
                TopicPartitionEqualityComparer.Instance);

        // 3. Pour chaque partition, calcule le lag
        foreach (var tp in topicPartitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var watermark = consumer.QueryWatermarkOffsets(tp, timeout);
            var high = watermark.High;

            if (high <= 0)
            {
                continue;
            }

            if (byPartition.TryGetValue(tp, out var tpoe) && !tpoe.Error.IsError)
            {
                var committedOffset = tpoe.Offset.Value;
                if (committedOffset >= 0 && high >= committedOffset)
                {
                    pending += (high - committedOffset);
                    totalCommitted += committedOffset;
                }
                else if (committedOffset < 0)
                {
                    // Aucun offset commité pour cette partition → on considère tout comme en attente.
                    pending += high.Value;
                }
            }
            else
            {
                // Pas d’information sur cette partition pour ce group → on considère tout comme en attente.
                pending += high.Value;
            }
        }

        return (pending, totalCommitted);
    }

    /// <summary>
    /// Fallback minimal : ne regarde pas du tout les offsets de consumer group.
    /// Estime juste le "volume brut" dans le log via les high watermarks.
    /// </summary>
    private static (long pending, long totalCommitted) GetPendingUsingConsumerOnly(
        IAdminClient adminClient,
        IConsumer<Ignore, Ignore> consumer,
        TopicBinding binding,
        TimeSpan timeout)
    {
        long pending = 0;

        var metadata = adminClient.GetMetadata(binding.Topic, timeout);
        var topicMetadata = metadata.Topics.SingleOrDefault(t => t.Topic == binding.Topic);

        if (topicMetadata == null || topicMetadata.Error.IsError)
        {
            return (0, 0);
        }

        var topicPartitions = topicMetadata.Partitions
            .Select(p => new TopicPartition(binding.Topic, p.PartitionId))
            .ToList();

        if (topicPartitions.Count == 0)
        {
            return (0, 0);
        }

        foreach (var tp in topicPartitions)
        {
            var watermark = consumer.QueryWatermarkOffsets(tp, timeout);
            var high = watermark.High;

            if (high >= 0)
            {
                // On compte le "volume brut" dans le log, sans savoir où en est le group SlimFaas
                pending += high.Value;
            }
        }

        // On ne connaît pas les offsets du group → 0 par convention
        return (pending, totalCommitted: 0);
    }

    private bool IsOutOfCooldown((string Topic, string Group) key, TopicBinding binding)
    {
        if (!_lastWakeUp.TryGetValue(key, out var last))
        {
            return true;
        }

        var cooldown = TimeSpan.FromSeconds(Math.Max(0, binding.CooldownSeconds));
        return DateTimeOffset.UtcNow - last >= cooldown;
    }

    /// <summary>
    /// Comparer pour indexer proprement un TopicPartition dans un dictionnaire.
    /// </summary>
    private sealed class TopicPartitionEqualityComparer : IEqualityComparer<TopicPartition>
    {
        public static readonly TopicPartitionEqualityComparer Instance = new();

        public bool Equals(TopicPartition? x, TopicPartition? y)
            => x?.Topic == y?.Topic && x?.Partition == y?.Partition;

        public int GetHashCode(TopicPartition obj)
            => HashCode.Combine(obj.Topic, obj.Partition.Value);
    }
}
