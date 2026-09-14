// LogTool: [LoggerMessage] definitions for Program.cs (#358, phase 3).
internal static partial class ProgramLog
{
    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Hello Called with name: {Name}")]
    internal static partial void LogHelloCalledWithName(this global::Microsoft.Extensions.Logging.ILogger logger, string name);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Download Called")]
    internal static partial void LogDownloadCalled(this global::Microsoft.Extensions.Logging.ILogger logger);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Fibonacci Called with input: {Input}")]
    internal static partial void LogFibonacciCalledWithInput(this global::Microsoft.Extensions.Logging.ILogger logger, int input);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Authorization Header: {Auth}")]
    internal static partial void LogAuthorizationHeader(this global::Microsoft.Extensions.Logging.ILogger logger, string auth);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Fibonacci output: {Output}")]
    internal static partial void LogFibonacciOutput(this global::Microsoft.Extensions.Logging.ILogger logger, int output);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Fibonacci4 Internal Called: {Input}")]
    internal static partial void LogFibonacci4InternalCalled(this global::Microsoft.Extensions.Logging.ILogger logger, int input);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in Fibonacci Recursive Internal")]
    internal static partial void LogErrorInFibonacciRecursiveInternal(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Fibonacci Private Event Called")]
    internal static partial void LogFibonacciPrivateEventCalled(this global::Microsoft.Extensions.Logging.ILogger logger);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Response status code: {StatusCode}")]
    internal static partial void LogResponseStatusCode(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Net.HttpStatusCode statusCode);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Fibonacci Internal Event End")]
    internal static partial void LogFibonacciInternalEventEnd(this global::Microsoft.Extensions.Logging.ILogger logger);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Fibonacci Recursive Internal Called: {Input}")]
    internal static partial void LogFibonacciRecursiveInternalCalled(this global::Microsoft.Extensions.Logging.ILogger logger, int input);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Current output: {Result}")]
    internal static partial void LogCurrentOutput(this global::Microsoft.Extensions.Logging.ILogger logger, int result);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in Fibonacci Recursive Internal")]
    internal static partial void LogErrorInFibonacciRecursiveInternal2(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

}
