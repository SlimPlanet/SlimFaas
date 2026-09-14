// LogTool: [LoggerMessage] definitions for ClusterMembershipAnnouncer.cs (#358, phase 3).
namespace SlimData
{
    internal static partial class ClusterMembershipAnnouncerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Membership announcement to {Candidate} timed out")]
        internal static partial void LogMembershipAnnouncementToTimedOut(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri candidate);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Membership announcement to {Candidate} failed")]
        internal static partial void LogMembershipAnnouncementToFailed(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Net.Http.HttpRequestException exception, global::System.Uri candidate);

    }
}
namespace SlimData
{
    internal static partial class ClusterMembershipAnnounceWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to announce this SlimData member to the cluster")]
        internal static partial void LogUnableToAnnounceThisSlimDataMember(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
