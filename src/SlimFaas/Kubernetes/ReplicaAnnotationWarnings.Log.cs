namespace SlimFaas.Kubernetes;

internal static partial class ReplicaAnnotationWarningsLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Function {Function} uses deprecated {Annotation}. Use {Replacement} for this replica minimum.")]
    internal static partial void LogDeprecatedReplicaAnnotation(this ILogger logger, string function, string annotation,
        string replacement);
}
