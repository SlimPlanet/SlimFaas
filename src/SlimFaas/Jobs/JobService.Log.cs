// LogTool: [LoggerMessage] definitions for JobService.cs (#358, phase 3).
namespace SlimFaas.Jobs
{
    internal static partial class JobServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Regex job pattern {Pattern} generated a timeout")]
        internal static partial void LogRegexJobPatternGeneratedTimeout(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Text.RegularExpressions.RegexMatchTimeoutException exception, string pattern);

    }
}
