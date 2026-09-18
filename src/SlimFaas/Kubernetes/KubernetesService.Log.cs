// LogTool: [LoggerMessage] definitions for KubernetesService.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class KubernetesServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Insufficient RBAC permissions to list Services in namespace {Namespace}. ServiceName will be null in PodInformation.")]
        internal static partial void LogInsufficientRBACPermissionsToListServices(this global::Microsoft.Extensions.Logging.ILogger logger, global::k8s.Autorest.HttpOperationException exception, string @namespace);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Error while listing Services in namespace {Namespace}. ServiceName will be null in PodInformation.")]
        internal static partial void LogErrorWhileListingServicesInNamespace(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string @namespace);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "TLS verification of the Kubernetes API server certificate is disabled (SlimFaas:KubernetesSkipTlsVerify=true). The SlimFaas service-account token can be intercepted on the path to the API server; enable this only for clusters whose API server certificate cannot be validated.")]
        internal static partial void LogKubernetesApiServerTlsVerificationDisabled(this global::Microsoft.Extensions.Logging.ILogger logger);

    }
}
