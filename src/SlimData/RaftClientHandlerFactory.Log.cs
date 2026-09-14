// LogTool: [LoggerMessage] definitions for RaftClientHandlerFactory.cs (#358, phase 3).
namespace SlimData
{
    internal static partial class RaftClientHandlerFactoryLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "RaftClientHandlerFactory.CreateHandler({Name}) with ConnectTimeout {ConnectTimeout}ms")]
        internal static partial void LogRaftClientHandlerFactoryCreateHandlerWithConnectTimeoutMs(this global::Microsoft.Extensions.Logging.ILogger logger, string name, int connectTimeout);

    }
}
