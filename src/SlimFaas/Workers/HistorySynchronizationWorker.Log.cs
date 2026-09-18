// LogTool: [LoggerMessage] definitions for HistorySynchronizationWorker.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class HistorySynchronizationWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "HistorySynchronizationWorker: ticksInDatabase is superior to now ticks {TimeSpan} for {Function}")]
        internal static partial void LogHistorySynchronizationWorkerTicksInDatabaseIsSuperiorToNow(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.TimeSpan timeSpan, string function);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "HistorySynchronizationWorker: ticksMemory is superior to now ticks {TimeSpan} for {Function}")]
        internal static partial void LogHistorySynchronizationWorkerTicksMemoryIsSuperiorToNow(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.TimeSpan timeSpan, string function);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "HistorySynchronizationWorker: Synchronizing history for {Function} to {Ticks} from Database")]
        internal static partial void LogHistorySynchronizationWorkerSynchronizingHistoryForToFrom(this global::Microsoft.Extensions.Logging.ILogger logger, string function, long ticks);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "HistorySynchronizationWorker: Synchronizing history for {Function} to {Ticks} from Memory")]
        internal static partial void LogHistorySynchronizationWorkerSynchronizingHistoryForToFrom2(this global::Microsoft.Extensions.Logging.ILogger logger, string function, long ticks);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Global Error in HistorySynchronizationWorker")]
        internal static partial void LogGlobalErrorInHistorySynchronizationWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
