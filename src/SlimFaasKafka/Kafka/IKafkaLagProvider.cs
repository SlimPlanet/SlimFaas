using SlimFaasKafka.Config;

namespace SlimFaasKafka.Kafka;

public interface IKafkaLagProvider
{
    /// <summary>
    /// Returns:
    ///  - pending: number of pending messages for this topic/group,
    ///  - totalCommitted: sum of committed offsets (for activity detection),
    ///  - usedAdmin: true if AdminClient was used (we have real group offsets).
    /// </summary>
    Task<(long pending, long totalCommitted, bool usedAdmin)> GetLagAsync(
        TopicBinding binding,
        KafkaOptions kafkaOptions,
        CancellationToken cancellationToken);
}
