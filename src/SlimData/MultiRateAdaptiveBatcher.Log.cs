using Microsoft.Extensions.Logging;

namespace SlimData;

internal static partial class MultiRateAdaptiveBatcherLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "SlimData command batch worker stopped unexpectedly; pending operations failed and the next enqueue can restart processing")]
    internal static partial void LogCommandBatchWorkerStopped(this ILogger logger, Exception exception);
}
