// LogTool: [LoggerMessage] definitions for KubernetesService.Jobs.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class KubernetesServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Job created with name: {JobName}")]
        internal static partial void LogJobCreatedWithName(this global::Microsoft.Extensions.Logging.ILogger logger, string jobName);

    }
}
