// LogTool: [LoggerMessage] definitions for KafkaMonitoringWorker.cs (#358, phase 3).
namespace SlimFaasKafka.Workers
{
    internal static partial class KafkaMonitoringWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "KafkaMonitoringWorker started. BootstrapServers = {BootstrapServers}, ClientId = {ClientId}")]
        internal static partial void LogKafkaMonitoringWorkerStartedBootstrapServersClientId(this global::Microsoft.Extensions.Logging.ILogger logger, string bootstrapServers, string clientId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "No bindings configured, skipping check")]
        internal static partial void LogNoBindingsConfiguredSkippingCheck(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Unexpected error while checking Kafka bindings")]
        internal static partial void LogUnexpectedErrorWhileCheckingKafkaBindings(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "KafkaMonitoringWorker stopped")]
        internal static partial void LogKafkaMonitoringWorkerStopped(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Binding topic={Topic}, group={Group}, function={Function} is using consumer-only heuristic (no visibility on consumer group offsets: recent-consumption keep-alive is limited).")]
        internal static partial void LogBindingTopicGroupFunctionIsUsing(this global::Microsoft.Extensions.Logging.ILogger logger, string topic, string group, string function);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Binding topic={Topic}, group={Group}, function={Function} has {Pending} pending messages, consumedDelta={ConsumedDelta}, recentActivity={RecentActivity}, usedAdmin={UsedAdmin}")]
        internal static partial void LogBindingTopicGroupFunctionHasPending(this global::Microsoft.Extensions.Logging.ILogger logger, string topic, string group, string function, long pending, long consumedDelta, bool recentActivity, bool usedAdmin);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Triggering wake up for function {Function} (pending={Pending}, recentActivity={RecentActivity}, reason={Reason}, usedAdmin={UsedAdmin}) on topic={Topic}, group={Group}")]
        internal static partial void LogTriggeringWakeUpForFunctionPending(this global::Microsoft.Extensions.Logging.ILogger logger, string function, long pending, bool recentActivity, string reason, bool usedAdmin, string topic, string group);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Cooldown still active for topic={Topic}, group={Group}, function={Function}")]
        internal static partial void LogCooldownStillActiveForTopicGroup(this global::Microsoft.Extensions.Logging.ILogger logger, string topic, string group, string function);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Unexpected error while checking binding topic={Topic}, group={Group}")]
        internal static partial void LogUnexpectedErrorWhileCheckingBindingTopic(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string topic, string group);

    }
}
