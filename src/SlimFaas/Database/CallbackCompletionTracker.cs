using System.Collections.Concurrent;

namespace SlimFaas.Database;

/// <summary>
/// Tracks async callback completions so the worker can detect when a 202-accepted
/// request's callback has arrived (or timed out).
/// </summary>
public sealed class CallbackCompletionTracker
{
    private readonly ConcurrentDictionary<string, int> _completedCallbacks = new();

    /// <summary>
    /// Signal that a callback has been received for the given element.
    /// Called from the callback endpoint.
    /// </summary>
    public void SignalCompleted(string elementId, int statusCode)
    {
        _completedCallbacks[elementId] = statusCode;
    }

    /// <summary>
    /// Try to consume a completion signal for the given element.
    /// Returns true if a callback was received, with the status code.
    /// </summary>
    public bool TryConsumeCompletion(string elementId, out int statusCode)
    {
        return _completedCallbacks.TryRemove(elementId, out statusCode);
    }

    /// <summary>
    /// Clean up stale entries (safety net).
    /// </summary>
    public void Cleanup(TimeSpan maxAge)
    {
        // The dictionary is self-cleaning via TryConsumeCompletion,
        // but if entries accumulate we can add TTL logic here if needed.
    }
}

