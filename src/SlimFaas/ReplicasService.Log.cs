// LogTool: [LoggerMessage] definitions for ReplicasService.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class ReplicasServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Time left without request for scale down {Deployment} is {TimeLeft}")]
        internal static partial void LogTimeLeftWithoutRequestForScale(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, global::System.TimeSpan timeLeft);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Skip scale-up for {Deployment} because a pod is blocked by Infrastructure Error: {PodFailureReason}: {PodFailureMessage}")]
        internal static partial void LogSkipScaleUpForBecausePod(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string? podFailureReason, string? podFailureMessage);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Scale {Deployment} from {CurrentScale} to {DesiredReplicas}")]
        internal static partial void LogScaleFromTo(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, int currentScale, int desiredReplicas);

    }
}
