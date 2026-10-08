// LogTool: [LoggerMessage] definitions for Program.cs (#358, phase 3).
internal static partial class ProgramLog
{
    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimData command protocol {Protocol}, assembly {AssemblyVersion}")]
    internal static partial void LogSlimDataCommandProtocolAssembly(this global::Microsoft.Extensions.Logging.ILogger logger, string protocol, string assemblyVersion);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "X-Forwarded-For honoured (one hop) for trusted proxies: {TrustedProxies}")]
    internal static partial void LogTrustedProxiesConfigured(this global::Microsoft.Extensions.Logging.ILogger logger, string trustedProxies);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Using orchestrator: {Orchestrator}")]
    internal static partial void LogUsingOrchestrator(this global::Microsoft.Extensions.Logging.ILogger logger, string orchestrator);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Using namespace: {Namespace}")]
    internal static partial void LogUsingNamespace(this global::Microsoft.Extensions.Logging.ILogger logger, string @namespace);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Current hostname: {Hostname}")]
    internal static partial void LogCurrentHostname(this global::Microsoft.Extensions.Logging.ILogger logger, string hostname);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Current SlimFaas pod: {PodName} {PodIp} {PodStarted}")]
    internal static partial void LogCurrentSlimFaasPod(this global::Microsoft.Extensions.Logging.ILogger logger, string podName, string podIp, bool? podStarted);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Waiting current pod to be ready")]
    internal static partial void LogWaitingCurrentPodToBeReady(this global::Microsoft.Extensions.Logging.ILogger logger);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Starting SlimFaas, coldstart: {ColdStart}")]
    internal static partial void LogStartingSlimFaasColdstart(this global::Microsoft.Extensions.Logging.ILogger logger, bool coldStart);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Waiting for at least 2 pods to be ready")]
    internal static partial void LogWaitingForAtLeast2Pods(this global::Microsoft.Extensions.Logging.ILogger logger);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Adding node {SlimDataEndpoint} {Hostname} {PodName}")]
    internal static partial void LogAddingNode(this global::Microsoft.Extensions.Logging.ILogger logger, string slimDataEndpoint, string hostname, string podName);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error adding node")]
    internal static partial void LogErrorAddingNode(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Starting node {PodName}")]
    internal static partial void LogStartingNode(this global::Microsoft.Extensions.Logging.ILogger logger, string podName);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Node started {PodName} {PublicEndpoint}")]
    internal static partial void LogNodeStarted(this global::Microsoft.Extensions.Logging.ILogger logger, string podName, string publicEndpoint);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimData state dir: {Directory}, hasExistingState={HasExistingState}, isFirstPod={IsFirstPod}, slimDataAllowColdStart={AllowColdStart}")]
    internal static partial void LogSlimDataStateDirHasExistingStateIsFirstPodSlimDataAllowColdStart(this global::Microsoft.Extensions.Logging.ILogger logger, string directory, bool hasExistingState, bool isFirstPod, bool allowColdStart);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimData configuration: {Key}={Value}")]
    internal static partial void LogSlimDataConfiguration(this global::Microsoft.Extensions.Logging.ILogger logger, string key, string @value);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "No Slimfaas ports")]
    internal static partial void LogNoSlimfaasPorts(this global::Microsoft.Extensions.Logging.ILogger logger);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Initializing Slimfaas ports")]
    internal static partial void LogInitializingSlimfaasPorts(this global::Microsoft.Extensions.Logging.ILogger logger);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Slimfaas listening on port {Port}")]
    internal static partial void LogSlimfaasListeningOnPort(this global::Microsoft.Extensions.Logging.ILogger logger, int port);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimFaas WebSocket listening on port {WsPort}")]
    internal static partial void LogSlimFaasWebSocketListeningOnPort(this global::Microsoft.Extensions.Logging.ILogger logger, int wsPort);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "CORS Allowing all origins")]
    internal static partial void LogCORSAllowingAllOrigins(this global::Microsoft.Extensions.Logging.ILogger logger);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimData port {SlimDataPort} will be excluded from rate limiting")]
    internal static partial void LogSlimDataPortWillBeExcludedFrom(this global::Microsoft.Extensions.Logging.ILogger logger, int slimDataPort);

    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to extract SlimData port from publicEndPoint: {PublicEndPoint}")]
    internal static partial void LogFailedToExtractSlimDataPortFrom(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string publicEndPoint);

}
