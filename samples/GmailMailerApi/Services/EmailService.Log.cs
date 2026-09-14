// LogTool: [LoggerMessage] definitions for EmailService.cs (#358, phase 3).
namespace GmailMailerApi.Services
{
    internal static partial class EmailServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Failed sending email via SMTP: {Message}")]
        internal static partial void LogFailedSendingEmailViaSMTP(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string message);

    }
}
