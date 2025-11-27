using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using SlimFaasKafka.Config;

namespace SlimFaasKafka.Kafka;

public sealed class KafkaLagProvider : IKafkaLagProvider
{
    private readonly ILogger<KafkaLagProvider> _logger;

    public KafkaLagProvider(ILogger<KafkaLagProvider> logger)
    {
        _logger = logger;
    }

    public async Task<(long pending, long totalCommitted, bool usedAdmin)> GetLagAsync(
        TopicBinding binding,
        KafkaOptions kafkaOptions,
        CancellationToken cancellationToken)
    {
        var adminConfig = new AdminClientConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers,
            ClientId = kafkaOptions.ClientId
        };

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers,
            GroupId = "slimfaaskafka-watermark",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = kafkaOptions.AllowAutoCreateTopics
        };

        using var adminClient = new AdminClientBuilder(adminConfig).Build();
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(consumerConfig).Build();

        var timeout = TimeSpan.FromSeconds(Math.Max(1, kafkaOptions.KafkaTimeoutSeconds));

        return await GetPendingWithAdminOrFallbackAsync(
                adminClient,
                consumer,
                binding,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
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

        var groupResult = results.Single();

        var byPartition = groupResult.Partitions
            .ToDictionary(
                p => p.TopicPartition,
                p => p,
                TopicPartitionEqualityComparer.Instance);

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
                    pending += high.Value;
                }
            }
            else
            {
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
                pending += high.Value;
            }
        }

        return (pending, totalCommitted: 0);
    }

    private sealed class TopicPartitionEqualityComparer : IEqualityComparer<TopicPartition>
    {
        public static readonly TopicPartitionEqualityComparer Instance = new();

        public bool Equals(TopicPartition? x, TopicPartition? y)
            => x?.Topic == y?.Topic && x?.Partition == y?.Partition;

        public int GetHashCode(TopicPartition obj)
            => HashCode.Combine(obj.Topic, obj.Partition.Value);
    }
}
