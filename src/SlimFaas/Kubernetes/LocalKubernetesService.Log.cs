// LogTool: [LoggerMessage] definitions for LocalKubernetesService.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class LocalKubernetesServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Local function {FunctionName} scaled to {Replicas}")]
        internal static partial void LogLocalFunctionScaledTo(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, int replicas);

    }
}
