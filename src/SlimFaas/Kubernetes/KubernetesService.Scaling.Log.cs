// LogTool: [LoggerMessage] definitions for KubernetesService.Scaling.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class KubernetesServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "name: {Name}\n annotations[Scale]: {Annotation}")]
        internal static partial void LogNameAnnotationsScale(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string name, string annotation);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error while scaling kubernetes deployment {RequestDeployment}")]
        internal static partial void LogErrorWhileScalingKubernetesDeployment(this global::Microsoft.Extensions.Logging.ILogger logger, global::k8s.Autorest.HttpOperationException exception, string requestDeployment);

    }
}
