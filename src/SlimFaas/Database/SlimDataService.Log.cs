// LogTool: [LoggerMessage] definitions for SlimDataService.cs (#358, phase 3).
namespace SlimFaas.Database
{
    internal static partial class SlimDataServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Trace,
            Message = "Queue command stage={Stage} Producer={ProducerId} Generation={GenerationId} Sequence={Sequence} BatchRequestId={BatchRequestId} OperationRequestId={OperationRequestId} Kind={Kind} Key={Key} ElementId={ElementId} StatusCode={StatusCode} BatchAttempt={BatchAttempt} Applied={Applied} DuplicateBatch={DuplicateBatch}")]
        internal static partial void LogQueueOperationStage(this global::Microsoft.Extensions.Logging.ILogger logger,
            string stage, string producerId, string generationId, long sequence, string batchRequestId,
            string operationRequestId, global::SlimData.SlimDataBatchOperationKind kind, string key, string elementId,
            int? statusCode, int batchAttempt, bool? applied, bool? duplicateBatch);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Trace,
            Message = "Queue reservation OperationRequestId={OperationRequestId} ElementId={ElementId} TryNumber={TryNumber} ReservedTarget={ReservedTarget} LastRetryTimeTicks={LastRetryTimeTicks} HttpTimeoutTicks={HttpTimeoutTicks}")]
        internal static partial void LogQueueReservationResult(this global::Microsoft.Extensions.Logging.ILogger logger,
            string operationRequestId, string elementId, int tryNumber, string reservedTarget, long lastRetryTimeTicks, long httpTimeoutTicks);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Trace,
            Message = "File admission map OperationRequestId={OperationRequestId} ElementId={ElementId} FileId={FileId} Action={Action}")]
        internal static partial void LogFileAdmissionQueueMap(this global::Microsoft.Extensions.Logging.ILogger logger,
            string operationRequestId, string elementId, string fileId, global::SlimData.FileAdmissionAction action);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Retrying the same ordered SlimData batch. Producer={ProducerId}, Sequence={Sequence}, Attempt={Attempt}")]
        internal static partial void LogRetryingTheSameOrderedSlimDataBatch(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string producerId, long sequence, int attempt);

    }
}
