// LogTool: [LoggerMessage] definitions for FibonacciWorker.cs (#358, phase 3).
internal static partial class FibonacciKafkaListenerLog
{
    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "FibonacciKafkaListener started. Topic={Topic}, GroupId={GroupId}")]
    internal static partial void LogFibonacciKafkaListenerStartedTopicGroupId(this global::Microsoft.Extensions.Logging.ILogger logger, string topic, string groupId);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Received message from Kafka: Topic={Topic}, Partition={Partition}, Offset={Offset}, Value={Value}")]
    internal static partial void LogReceivedMessageFromKafkaTopicPartition(this global::Microsoft.Extensions.Logging.ILogger logger, string topic, int partition, long offset, string value);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Computed Fibonacci({N}) = {Fib}")]
    internal static partial void LogComputedFibonacci(this global::Microsoft.Extensions.Logging.ILogger logger, int n, long fib);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Kafka consume error")]
    internal static partial void LogKafkaConsumeError(this global::Microsoft.Extensions.Logging.ILogger logger, global::Confluent.Kafka.ConsumeException exception);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "FibonacciKafkaListener stopped.")]
    internal static partial void LogFibonacciKafkaListenerStopped(this global::Microsoft.Extensions.Logging.ILogger logger);

}
