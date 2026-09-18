// LogTool: [LoggerMessage] definitions for KafkaLagProvider.cs (#358, phase 3).
namespace SlimFaasKafka.Kafka
{
    internal static partial class KafkaLagProviderLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "AdminClient.ListConsumerGroupOffsetsAsync failed for group '{GroupId}', topic '{Topic}'. Falling back to consumer-only heuristic (no visibility on consumer group offsets).")]
        internal static partial void LogAdminClientListConsumerGroupOffsetsAsyncFailedForGroupTopic(this global::Microsoft.Extensions.Logging.ILogger logger, global::Confluent.Kafka.Admin.ListConsumerGroupOffsetsException exception, string groupId, string topic);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Not authorized to read consumer group offsets for group '{GroupId}', topic '{Topic}'. Falling back to consumer-only heuristic (no visibility on consumer group offsets).")]
        internal static partial void LogNotAuthorizedToReadConsumerGroup(this global::Microsoft.Extensions.Logging.ILogger logger, global::Confluent.Kafka.KafkaException exception, string groupId, string topic);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unexpected error while using AdminClient for group '{GroupId}', topic '{Topic}'. Falling back to consumer-only heuristic.")]
        internal static partial void LogUnexpectedErrorWhileUsingAdminClientFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string groupId, string topic);

    }
}
