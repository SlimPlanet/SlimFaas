// LogTool: [LoggerMessage] definitions for DockerService.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class DockerServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "[Startup] PurgeStaleTemplates failed (continuing)")]
        internal static partial void LogStartupPurgeStaleTemplatesFailedContinuing(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Startup] Removing stale template id={Id} name={Name} state={State}")]
        internal static partial void LogStartupRemovingStaleTemplateIdName(this global::Microsoft.Extensions.Logging.ILogger logger, string id, string? name, string state);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Scale] {Deployment} ns={Ns} desired={Desired}")]
        internal static partial void LogScaleNsDesired(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string ns, int desired);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "[Scale] {Deployment} desired=0: template creation failed, skip cleanup this tick.")]
        internal static partial void LogScaleDesired0TemplateCreationFailed(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "[Scale] {Deployment} desired=0: cannot inspect source; skip cleanup this tick.")]
        internal static partial void LogScaleDesired0CannotInspectSource(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Scale] {Deployment} desired=0: no source found to build template; continuing cleanup.")]
        internal static partial void LogScaleDesired0NoSourceFound(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Scale] Stop/Remove (desired=0) {Deployment} id={Id} name={Name} state={State}")]
        internal static partial void LogScaleStopRemoveDesired0Id(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string id, string? name, string state);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Scale] {Deployment} ns={Ns} scale-up: replica récent détecté (state={State}) → on attend")]
        internal static partial void LogScaleNsScaleUpReplicaCent(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string ns, string state);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Scale] {Deployment} ns={Ns} scale-up: {Existing} actif(s) ≥ desired={Desired} → skip")]
        internal static partial void LogScaleNsScaleUpActifDesired(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string ns, int existing, int desired);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[withTemplate] Count={Count}")]
        internal static partial void LogWithTemplateCount(this global::Microsoft.Extensions.Logging.ILogger logger, int count);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "[Scale] {Deployment} ns={Ns} scale-up failed: no container or template found")]
        internal static partial void LogScaleNsScaleUpFailedNo(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string ns);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Scale] Skip down {Deployment} (desired={Desired}) -> warmup/starting; since {StartedAt:u}")]
        internal static partial void LogScaleSkipDownDesiredWarmupStarting(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, int desired, global::System.DateTimeOffset? startedAt);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Scale] Stop/Remove {Deployment} id={Id} name={Name}")]
        internal static partial void LogScaleStopRemoveIdName(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string id, string? name);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "DockerService: Deployment {Deployment} has {Count} running non-template containers")]
        internal static partial void LogDockerServiceDeploymentHasRunningNonTemplate(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, int count);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "DockerService: Pod {Pod} IP={IP} Ports=[{Ports}] Ready={Ready}")]
        internal static partial void LogDockerServicePodIPPortsReady(this global::Microsoft.Extensions.Logging.ILogger logger, string? pod, string iP, string ports, bool ready);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Job create: name={Name} id={Id}")]
        internal static partial void LogJobCreateNameId(this global::Microsoft.Extensions.Logging.ILogger logger, string name, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Job started: {Name} state={State} exit={Exit} error={Err}")]
        internal static partial void LogJobStartedStateExitError(this global::Microsoft.Extensions.Logging.ILogger logger, string name, bool? state, int? exit, string? err);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Failed to start job {Job}. Logs tail:\n{Logs}")]
        internal static partial void LogFailedToStartJobLogsTail(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Net.Http.HttpRequestException exception, string job, string logs);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "DeleteJob: container '{JobName}' not found.")]
        internal static partial void LogDeleteJobContainerNotFound(this global::Microsoft.Extensions.Logging.ILogger logger, string jobName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Template] Created template for {Deployment} in ns={Ns} name={Name}")]
        internal static partial void LogTemplateCreatedTemplateForInNs(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string ns, string name);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "[Scale-UP] Started replica {Deployment} id={Id} (template label removed)")]
        internal static partial void LogScaleUPStartedReplicaIdTemplate(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Start failed for replica {Name} (id={Id}). Logs tail:\n{Logs}")]
        internal static partial void LogStartFailedForReplicaIdLogs(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Net.Http.HttpRequestException exception, string name, string id, string logs);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Image pull failed or skipped for {Image}")]
        internal static partial void LogImagePullFailedOrSkippedFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string image);

    }
}
