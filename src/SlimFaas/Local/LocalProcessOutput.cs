using Microsoft.Extensions.Logging;

namespace SlimFaas.Local;

/// <summary>Optional process log storage must not terminate the process supervisor.</summary>
internal sealed class LocalProcessOutput(
    TextWriter writer,
    string tag,
    string path,
    ILogger? logger = null) : IDisposable
{
    private readonly ILogger _logger = logger ?? LocalProcessOutputLog.ConsoleLogger;
    private long _failedWrites;
    private bool _disposed;

    internal void WriteLine(string value)
    {
        if (_disposed)
            return;
        try
        {
            writer.WriteLine(value);
            if (_failedWrites > 0)
            {
                _logger.LogProcessFileLogResumed(tag, path, _failedWrites);
                _failedWrites = 0;
            }
        }
        catch (IOException exception)
        {
            ReportFailure(exception);
        }
    }

    private void ReportFailure(IOException exception)
    {
        if (_failedWrites++ == 0)
            _logger.LogProcessFileLogUnavailable(exception, tag, path);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            writer.Dispose();
        }
        catch (IOException exception)
        {
            ReportFailure(exception);
        }
    }
}
