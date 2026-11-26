using System.Collections.Concurrent;

namespace SlimFaas.Kubernetes;

internal sealed class ReplicaAnnotationWarnings(ILogger logger)
{
    private readonly ConcurrentDictionary<(string Function, string Annotation), byte> _reported = new();

    internal void Report(string function, string annotation, string replacement)
    {
        if (logger.IsEnabled(LogLevel.Warning) && _reported.TryAdd((function, annotation), 0))
            logger.LogDeprecatedReplicaAnnotation(function, annotation, replacement);
    }
}
