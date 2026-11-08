using Microsoft.Extensions.Logging;

namespace SlimFaas.Local;

internal static partial class LocalProcessOutputLog
{
    internal static ILogger ConsoleLogger { get; } = new ProcessConsoleLogger();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Process file log is unavailable. Process={Tag} Path={Path}. Console output continues; file output may be incomplete.")]
    internal static partial void LogProcessFileLogUnavailable(this ILogger logger, Exception exception, string tag, string path);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Process file log resumed. Process={Tag} Path={Path} FailedWrites={FailedWrites}. File output during the failure may be incomplete.")]
    internal static partial void LogProcessFileLogResumed(this ILogger logger, string tag, string path, long failedWrites);

    // The Local CLI deliberately owns its console output rather than a web-host
    // logging provider. Keep diagnostics source generated without a second host
    // or a disposable logger factory in process-output callbacks.
    private sealed class ProcessConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try
            {
                Console.Error.WriteLine(formatter(state, exception));
            }
            catch (IOException)
            {
                // A closed console pipe during shutdown cannot report its own
                // failure, and must not turn an optional log sink into a crash.
            }
        }
    }
}
