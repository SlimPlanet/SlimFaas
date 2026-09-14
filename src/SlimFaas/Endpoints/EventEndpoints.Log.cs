// LogTool: [LoggerMessage] definitions for EventEndpoints.cs (#358, phase 3).
namespace SlimFaas.Endpoints
{
    internal static partial class EventEndpointsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Receiving event: {EventName}")]
        internal static partial void LogReceivingEvent(this global::Microsoft.Extensions.Logging.ILogger logger, string eventName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Publish-event {EventName} : Return 404 from event")]
        internal static partial void LogPublishEventReturn404FromEvent(this global::Microsoft.Extensions.Logging.ILogger logger, string eventName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Publish-event list {EventName} : Deployment {Deployment}")]
        internal static partial void LogPublishEventListDeployment(this global::Microsoft.Extensions.Logging.ILogger logger, string eventName, string deployment);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Publish-event pod {Ready} endpoint {EndpointReady} IP: {Deployment}")]
        internal static partial void LogPublishEventPodEndpointIP(this global::Microsoft.Extensions.Logging.ILogger logger, bool? ready, bool endpointReady, string deployment);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Publish-event {EventName} : Deployment {Deployment} Pod {PodName} is ready: {PodReady}")]
        internal static partial void LogPublishEventDeploymentPodIsReady(this global::Microsoft.Extensions.Logging.ILogger logger, string eventName, string deployment, string podName, bool? podReady);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Sending event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent}")]
        internal static partial void LogSendingEventToAtWithPath(this global::Microsoft.Extensions.Logging.ILogger logger, string eventName, string functionDeployment, string baseUrl, string functionPath, string uriComponent);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Response from event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent} is {StatusCode}")]
        internal static partial void LogResponseFromEventToAtWith(this global::Microsoft.Extensions.Logging.ILogger logger, string eventName, string functionDeployment, string baseUrl, string functionPath, string uriComponent, global::System.Net.HttpStatusCode statusCode);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in sending event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent}")]
        internal static partial void LogErrorInSendingEventToAt(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string eventName, string functionDeployment, string baseUrl, string functionPath, string uriComponent);

    }
}
