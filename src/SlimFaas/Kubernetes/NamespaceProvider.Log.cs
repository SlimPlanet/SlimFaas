// LogTool: [LoggerMessage] definitions for NamespaceProvider.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class NamespaceProviderLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Namespace resolved from Kubernetes service account: {ResolvedNamespace} (configured: {ConfiguredNamespace})")]
        internal static partial void LogNamespaceResolvedFromKubernetesServiceAccount(this global::Microsoft.Extensions.Logging.ILogger logger, string resolvedNamespace, string configuredNamespace);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Using configured namespace: {Namespace}")]
        internal static partial void LogUsingConfiguredNamespace(this global::Microsoft.Extensions.Logging.ILogger logger, string @namespace);

    }
}
