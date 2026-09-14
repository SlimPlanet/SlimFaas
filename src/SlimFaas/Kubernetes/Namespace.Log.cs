// LogTool: [LoggerMessage] definitions for Namespace.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class NamespaceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Namespace file found: {NamespaceName}")]
        internal static partial void LogNamespaceFileFound(this global::Microsoft.Extensions.Logging.ILogger logger, string namespaceName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Namespace file not found")]
        internal static partial void LogNamespaceFileNotFound(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error reading namespace file")]
        internal static partial void LogErrorReadingNamespaceFile(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
