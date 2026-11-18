using System.Collections.Concurrent;
using DotNext;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SlimFaas.Tests.Integration;

internal sealed class RaftMembershipDiagnostics(ConcurrentQueue<string> messages, int node)
    : Disposable, ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferedLogger(messages, node, categoryName);

    private sealed class BufferedLogger(ConcurrentQueue<string> messages, int node, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            messages.Enqueue($"{DateTime.UtcNow:O} node={node} {category} [{logLevel}/{eventId.Id}] " +
                $"{formatter(state, exception)}{Environment.NewLine}{exception}");
        }
    }
}
