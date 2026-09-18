// LogTool: [LoggerMessage] definitions for KubernetesService.Deployments.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class KubernetesServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error while listing kubernetes functions")]
        internal static partial void LogErrorWhileListingKubernetesFunctions(this global::Microsoft.Extensions.Logging.ILogger logger, global::k8s.Autorest.HttpOperationException exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error while adding deployment {Deployment}")]
        internal static partial void LogErrorWhileAddingDeployment(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string deployment);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unknown prefix '{Prefix}' for path '{Path}'. The default (Public) visibility will be used.")]
        internal static partial void LogUnknownPrefixForPathTheDefault(this global::Microsoft.Extensions.Logging.ILogger logger, string prefix, string path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unknown prefix '{Prefix}' for event '{EventName}'. The default (Public) visibility will be used.")]
        internal static partial void LogUnknownPrefixForEventTheDefault(this global::Microsoft.Extensions.Logging.ILogger logger, string prefix, string eventName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "name: {Name}\\n annotations[Schedule]: {Annotation}")]
        internal static partial void LogNameAnnotationsSchedule(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string name, string annotation);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "name: {Name}\\n annotations[Configuration]: {Configuration}")]
        internal static partial void LogNameAnnotationsConfiguration(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string name, string configuration);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error while adding statefulset {Deployment}")]
        internal static partial void LogErrorWhileAddingStatefulset(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string deployment);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error while mapping pod informations for pod {PodName}: {Error}")]
        internal static partial void LogErrorWhileMappingPodInformationsFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string podName, string error);

    }
}
