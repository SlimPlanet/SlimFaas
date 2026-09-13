// LogTool: [LoggerMessage] definitions for SlimDataService.cs (#358, phase 3).
namespace SlimFaas.Database
{
    internal static partial class SlimDataServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Retrying the same ordered SlimData batch. Producer={ProducerId}, Sequence={Sequence}, Attempt={Attempt}")]
        internal static partial void LogRetryingTheSameOrderedSlimDataBatch(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string producerId, long sequence, int attempt);

    }
}
