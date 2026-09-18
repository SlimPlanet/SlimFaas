// LogTool: [LoggerMessage] definitions for KubernetesService.JobsConfiguration.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class KubernetesServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error while listing kubernetes cron jobs")]
        internal static partial void LogErrorWhileListingKubernetesCronJobs(this global::Microsoft.Extensions.Logging.ILogger logger, global::k8s.Autorest.HttpOperationException exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "CronJob {CronJobName} is not suspended, skipping it in the SlimFaas job configuration.")]
        internal static partial void LogCronJobIsNotSuspendedSkippingIt(this global::Microsoft.Extensions.Logging.ILogger logger, string cronJobName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error parsing SlimFaas/Schedules annotation for CronJob {CronJobName}")]
        internal static partial void LogErrorParsingSlimFaasSchedulesAnnotationFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string cronJobName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "JobConfiguration: {JobConfiguration}")]
        internal static partial void LogJobConfiguration(this global::Microsoft.Extensions.Logging.ILogger logger, global::SlimFaas.Kubernetes.SlimfaasJob jobConfiguration);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "No SlimFaas job configurations found in the cluster.")]
        internal static partial void LogNoSlimFaasJobConfigurationsFoundIn(this global::Microsoft.Extensions.Logging.ILogger logger);

    }
}
