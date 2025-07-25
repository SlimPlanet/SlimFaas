using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNext;
using MemoryPack;
using SlimData;
using SlimData.Commands;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Queue;

namespace SlimFaas;

public enum FunctionType
{
    Sync,
    Async,
    Wake,
    Status,
    Publish,
    Job,
    NotAFunction,
    Queue
}

public record FunctionStatus(int NumberReady,
    int NumberRequested,
    string PodType,
    string Visibility, string Name);

[JsonSerializable(typeof(FunctionStatus))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class FunctionStatusSerializerContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(List<FunctionStatus>))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ListFunctionStatusSerializerContext : JsonSerializerContext
{
}

public class SlimProxyMiddleware(RequestDelegate next, ISlimFaasQueue slimFaasQueue, IWakeUpFunction wakeUpFunction,
    ILogger<SlimProxyMiddleware> logger, ISlimFaasPorts slimFaasPorts ,
    int timeoutWaitWakeSyncFunctionMilliSecond =
        EnvironmentVariables.SlimProxyMiddlewareTimeoutWaitWakeSyncFunctionMilliSecondsDefault,
    string slimFaasSubscribeEventsDefault = EnvironmentVariables.SlimFaasSubscribeEventsDefault)

{
    private const string AsyncFunction = "/async-function";
    private const string Queue = "/queue";
    private const string StatusFunction = "/status-function";
    private const string WakeFunction = "/wake-function";
    private const string Function = "/function";
    private const string PublishEvent = "/publish-event";
    private const string Job = "/job";

    private readonly int _timeoutMaximumWaitWakeSyncFunctionMilliSecond = EnvironmentVariables.ReadInteger(logger,
        EnvironmentVariables.TimeMaximumWaitForAtLeastOnePodStartedForSyncFunction,
        timeoutWaitWakeSyncFunctionMilliSecond);

    private readonly IDictionary<string, IList<string>> _slimFaasSubscribeEvents = EnvironmentVariables.ReadSlimFaasSubscribeEvents(logger,
        EnvironmentVariables.SlimFaasSubscribeEvents,
        slimFaasSubscribeEventsDefault);



    public async Task InvokeAsync(HttpContext context,
        HistoryHttpMemoryService historyHttpService,
        ISendClient sendClient,
        IReplicasService replicasService,
        IJobService jobService,
        IServiceProvider serviceProvider)
    {

        if (!HostPort.IsSamePort(context.Request.Host.Port, slimFaasPorts.Ports.ToArray()))
        {
            await next(context);
            return;
        }

        HttpRequest contextRequest = context.Request;
        HttpResponse contextResponse = context.Response;

        if(contextRequest.Path.StartsWithSegments("/status-functions"))
        {
            IList<FunctionStatus> functionStatuses = replicasService.Deployments.Functions.Select(MapToFunctionStatus).ToList();
            context.Response.StatusCode = 200;
            await contextResponse.WriteAsJsonAsync(functionStatuses,
                ListFunctionStatusSerializerContext.Default.ListFunctionStatus);

            return;
        }

        (string functionPath, string functionName, FunctionType functionType) = GetFunctionInfo(logger, contextRequest);

        switch (functionType)
        {
            case FunctionType.NotAFunction:
                await next(context);
                break;
            case FunctionType.Queue:
                if (contextRequest.Method == HttpMethods.Get)
                {
                    ISupplier<SlimDataPayload> simplePersistentState = serviceProvider.GetRequiredService<ISupplier<SlimDataPayload>>();

                    SlimDataPayload data = simplePersistentState.Invoke();
                    var result = new List<QueueElement>();

                    if (!data.Queues.TryGetValue(functionName, out List<QueueElement>? value) && value !=null)
                    {
                         var queue = SlimFaasQueuesData.MapToNewModel(value);

                         contextResponse.StatusCode = (int)HttpStatusCode.OK;
                         await contextResponse.WriteAsJsonAsync(queue,
                             SlimFaasQueuesDataSerializerContext.Default.SlimFaasQueuesData);
                    }

                    contextResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                break;
            case FunctionType.Job:
                await BuildJobResponse(context, replicasService, jobService, contextRequest, contextResponse, functionName, functionPath);
                break;
            case FunctionType.Wake:
                BuildWakeResponse(replicasService, wakeUpFunction, functionName, contextResponse);
                break;
            case FunctionType.Status:
                BuildStatusResponse(replicasService, functionName, contextResponse);
                break;
            case FunctionType.Sync:
                await BuildSyncResponseAsync(logger, context, historyHttpService, sendClient, replicasService, jobService, functionName,
                    functionPath);
                break;
            case FunctionType.Publish:
                await BuildPublishResponseAsync(context, historyHttpService, sendClient, replicasService, jobService, functionName,
                    functionPath);
                break;

            case FunctionType.Async:
            default:
                {
                    await BuildAsyncResponseAsync(logger, context, replicasService, jobService, functionName, functionPath);
                    break;
                }
        }
    }

    private async Task BuildJobResponse(HttpContext context, IReplicasService replicasService, IJobService jobService,
        HttpRequest contextRequest, HttpResponse contextResponse, string functionName, string functionPath)
    {
        if (contextRequest.Method == HttpMethods.Put || contextRequest.Method == HttpMethods.Patch)
        {
            contextResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            return;
        }

        if (contextRequest.Method == HttpMethods.Post)
        {
            CreateJob? createJob =
                await contextRequest.ReadFromJsonAsync(CreateJobSerializerContext.Default.CreateJob);
            if (createJob == null)
            {
                contextResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            logger.LogInformation("Create job {JobName} with {CreateJob}", functionName, createJob);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Create job details {CreateJob} ", JsonSerializer.Serialize(createJob,
                    CreateJobSerializerContext.Default.CreateJob));
            }

            bool isMessageComeFromNamespaceInternal =
                MessageComeFromNamespaceInternal(logger, context, replicasService, jobService);
            var result =
                await jobService.EnqueueJobAsync(functionName, createJob, isMessageComeFromNamespaceInternal);
            if (result.Code >= 400)
            {
                contextResponse.StatusCode = result.Code;
                logger.LogWarning("Job HTTP Status {HttpStatusCode} with error {ErrorKey}",
                    contextResponse.StatusCode, result.ErrorKey);
                return;
            }
            contextResponse.StatusCode = 202;
            await contextResponse.WriteAsJsonAsync(new EnqueueJobResultSuccess(result.ElementId),
                EnqueueJobResultSuccessSerializerContext.Default.EnqueueJobResultSuccess);

            return;
        }
        if (contextRequest.Method == HttpMethods.Get)
        {
            var jobs = await jobService.ListJobAsync(functionName);
            contextResponse.StatusCode = 200;
            await contextResponse.WriteAsJsonAsync(jobs,
                JobListResultSerializerContext.Default.ListJobListResult);
            return;
        }
        if (contextRequest.Method == HttpMethods.Delete)
        {
            string elementId = functionPath.Replace("/", "");
            bool isMessageComeFromNamespaceInternal =
                MessageComeFromNamespaceInternal(logger, context, replicasService, jobService);
            logger.LogInformation("Delete job {JobName} with {Id}", functionName, elementId);
            bool isSuccess = await jobService.DeleteJobAsync(functionName, elementId, isMessageComeFromNamespaceInternal);
            contextResponse.StatusCode = isSuccess ? 200 : 404;
            return;
        }
    }

    private static bool MessageComeFromNamespaceInternal(ILogger<SlimProxyMiddleware> logger, HttpContext context, IReplicasService replicasService, IJobService jobService, DeploymentInformation? currentFunction=null)
    {
        List<string> podIps;

        podIps = replicasService.Deployments.Functions.Where(f => f.Trust == FunctionTrust.Trusted).Select(p => p.Pods)
            .SelectMany(p => p)
            .Select(p => p.Ip)
            .ToList();

        podIps.AddRange(jobService.Jobs.Select(job => job.Ips).SelectMany(ip => ip));
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "";
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "";
        logger.LogDebug("ForwardedFor: {ForwardedFor}, RemoteIp: {RemoteIp}", forwardedFor, remoteIp);
        if(logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var podIp in podIps)
            {
                logger.LogDebug("PodIp: {PodIp}", podIp);
            }
        }

        if (IsInternalIp(forwardedFor, podIps) || IsInternalIp(remoteIp, podIps))
        {
            logger.LogDebug("Request come from internal namespace ForwardedFor: {ForwardedFor}, RemoteIp: {RemoteIp}", forwardedFor, remoteIp);
            return true;
        }
        logger.LogDebug("Request come from external namespace ForwardedFor: {ForwardedFor}, RemoteIp: {RemoteIp}", forwardedFor, remoteIp);

        return false;
    }

    private static bool IsInternalIp(string? ipAddress, IList<string> podIps)
    {
        if (ipAddress == null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(ipAddress))
        {
            return false;
        }

        foreach (string podIp in podIps)
        {
            if (string.IsNullOrEmpty(podIp))
            {
                continue;
            }
            if (ipAddress.Contains(podIp))
            {
                return true;
            }
        }

        return false;
    }

    private static void BuildStatusResponse(IReplicasService replicasService,
        string functionName, HttpResponse contextResponse)
    {
        DeploymentInformation? functionDeploymentInformation = SearchFunction(replicasService, functionName);
        if (functionDeploymentInformation != null)
        {
            FunctionStatus functionStatus = MapToFunctionStatus(functionDeploymentInformation);
            contextResponse.StatusCode = 200;
            contextResponse.WriteAsJsonAsync(functionStatus,
                FunctionStatusSerializerContext.Default.FunctionStatus);
        }
        else
        {
            contextResponse.StatusCode = 404;
        }
    }

    private static FunctionStatus MapToFunctionStatus(DeploymentInformation functionDeploymentInformation)
    {
        int numberReady = functionDeploymentInformation.Pods.Count(p => p.Ready.HasValue && p.Ready.Value);
        int numberRequested =
            functionDeploymentInformation.Replicas;
        var functionStatus = new FunctionStatus(numberReady, numberRequested,
            functionDeploymentInformation.PodType.ToString(),
            functionDeploymentInformation.Visibility.ToString(), functionDeploymentInformation.Deployment);
        return functionStatus;
    }

    private static void BuildWakeResponse(IReplicasService replicasService, IWakeUpFunction wakeUpFunction,
        string functionName, HttpResponse contextResponse)
    {
        DeploymentInformation? function = SearchFunction(replicasService, functionName);
        if (function != null)
        {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            wakeUpFunction.FireAndForgetWakeUpAsync(functionName);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            contextResponse.StatusCode = 204;
        }
        else
        {
            contextResponse.StatusCode = 404;
        }
    }

    private static List<DeploymentInformation> SearchFunctions(ILogger<SlimProxyMiddleware> logger, HttpContext context, IReplicasService replicasService, IJobService jobService, string eventName)
    {
        // example: "Public:my-event-name1,Private:my-event-name2,my-event-name3"
        var result = new List<DeploymentInformation>();
        foreach (DeploymentInformation deploymentInformation in replicasService.Deployments.Functions)
        {
            var subscribeEvent = deploymentInformation.SubscribeEvents?.FirstOrDefault(se => se.Name == eventName);
            if (subscribeEvent == null)
            {
                continue;
            }
            logger.LogDebug("Deployment {DeploymentInformation} SubscribeEvent {SubscribeEvent} {SubscribeEventVisibility}", deploymentInformation.Deployment, subscribeEvent.Name, subscribeEvent.Visibility.ToString());
            if (subscribeEvent.Visibility == FunctionVisibility.Public)
            {
                logger.LogDebug("Deployment ADDED FOR {DeploymentInformation} SubscribeEvent {SubscribeEvent} {SubscribeEventVisibility}", deploymentInformation.Deployment, subscribeEvent.Name, subscribeEvent.Visibility.ToString());
                result.Add(deploymentInformation);
                continue;
            }

            bool internalMessage = MessageComeFromNamespaceInternal(logger, context, replicasService, jobService,
                deploymentInformation);
            if (internalMessage)
            {
                result.Add(deploymentInformation);
            }
        }
        return result;
    }

    private static DeploymentInformation? SearchFunction(IReplicasService replicasService, string functionName)
    {
        DeploymentInformation? function =
            replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == functionName);
        return function;
    }

    private static FunctionVisibility GetFunctionVisibility(ILogger<SlimProxyMiddleware> logger, DeploymentInformation function, string path)
    {
        if (!(function.PathsStartWithVisibility?.Count > 0))
        {
            return function.Visibility;
        }

        foreach (var pathStartWith in function.PathsStartWithVisibility)
        {
            if (path.ToLowerInvariant().StartsWith(pathStartWith.Path))
            {
                return pathStartWith.Visibility;
            }
            logger.LogWarning("PathStartWithVisibility {PathStartWith} should be prefixed by Public: or Private:", pathStartWith);
        }
        return function.Visibility;
    }

    private async Task BuildAsyncResponseAsync(ILogger<SlimProxyMiddleware> logger, HttpContext context, IReplicasService replicasService, IJobService jobService, string functionName,
        string functionPath)
    {
        DeploymentInformation? function = SearchFunction(replicasService, functionName);
        if (function == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var visibility = GetFunctionVisibility(logger, function, functionPath);

        if (visibility == FunctionVisibility.Private && !MessageComeFromNamespaceInternal(logger, context, replicasService, jobService, function))
        {
            context.Response.StatusCode = 404;
            return;
        }
        CustomRequest customRequest =
            await InitCustomRequest(context, context.Request, functionName, functionPath);

        var bin = MemoryPackSerializer.Serialize(customRequest);
        var defaultAsync = function.Configuration.DefaultAsync;
        await slimFaasQueue.EnqueueAsync(functionName, bin, new RetryInformation(defaultAsync.TimeoutRetries, defaultAsync.HttpTimeout, defaultAsync.HttpStatusRetries));

        context.Response.StatusCode = 202;
    }

    private async Task BuildPublishResponseAsync(HttpContext context, HistoryHttpMemoryService historyHttpService,
        ISendClient sendClient, IReplicasService replicasService, IJobService jobService, string eventName, string functionPath)
    {
        logger.LogDebug("Receiving event: {EventName}", eventName);
        var functions = SearchFunctions(logger, context, replicasService, jobService, eventName);
        var slimFaasSubscribeEvents = _slimFaasSubscribeEvents.Where(s => s.Key == eventName);
        if (functions.Count <= 0 && !slimFaasSubscribeEvents.Any())
        {
            logger.LogDebug("Publish-event {EventName} : Return 404 from event", eventName);
            context.Response.StatusCode = 404;
            return;
        }
        var lastSetTicks = DateTime.UtcNow.Ticks;

        List<DeploymentInformation> calledFunctions = new();
        CustomRequest customRequest =
            await InitCustomRequest(context, context.Request, "", functionPath);

        List<Task> tasks = new();
        var queryString = context.Request.QueryString.ToUriComponent();
        foreach (DeploymentInformation function in functions)
        {
            logger.LogDebug("Publish-event list {EventName} : Deployment {Deployment}", eventName, function.Deployment);
            foreach (var pod in function.Pods)
            {
                logger.LogDebug("Publish-event pod {Ready} endpoint {EndpointReady} IP: {Deployment}", pod.Ready, function.EndpointReady, pod.Ip);
                if (pod.Ready is not true || !function.EndpointReady)
                {
                    continue;
                }

                if (!calledFunctions.Contains(function))
                {
                    calledFunctions.Add(function);
                }
                logger.LogInformation("Publish-event {EventName} : Deployment {Deployment} Pod {PodName} is ready: {PodReady}", eventName, function.Deployment, pod.Name, pod.Ready);
                historyHttpService.SetTickLastCall(function.Deployment, lastSetTicks);

                string baseFunctionPodUrl =
                    Environment.GetEnvironmentVariable(EnvironmentVariables.BaseFunctionPodUrl) ??
                    EnvironmentVariables.BaseFunctionPodUrlDefault;

                var baseUrl = SlimDataEndpoint.Get(pod, baseFunctionPodUrl);
                logger.LogDebug("Sending event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent}", eventName, function.Deployment, baseUrl, functionPath, context.Request.QueryString.ToUriComponent());
                Task task = SendRequest(queryString, sendClient, customRequest with {FunctionName =  function.Deployment}, baseUrl, logger, eventName, function.Configuration.DefaultPublish);
                tasks.Add(task);
            }
        }

        foreach (KeyValuePair<string,IList<string>> slimFaasSubscribeEvent in slimFaasSubscribeEvents)
        {
            foreach (string baseUrl in slimFaasSubscribeEvent.Value)
            {
                logger.LogDebug("Sending event {EventName} to {BaseUrl} with path {FunctionPath} and query {UriComponent}", eventName, baseUrl, functionPath, context.Request.QueryString.ToUriComponent());
                Task task = SendRequest(queryString, sendClient, customRequest with {FunctionName = ""}, baseUrl, logger, eventName, new SlimFaasDefaultConfiguration());
                tasks.Add(task);
            }
        }

        while (tasks.Any(t => !t.IsCompleted) && !context.RequestAborted.IsCancellationRequested)
        {
            await Task.Delay(10, context.RequestAborted);
            bool isOneSecondElapsed = new DateTime(lastSetTicks, DateTimeKind.Utc) < DateTime.UtcNow.AddSeconds(-1);
            if (!isOneSecondElapsed)
            {
                continue;
            }

            lastSetTicks = DateTime.UtcNow.Ticks;
            foreach (DeploymentInformation function in calledFunctions)
            {
                historyHttpService.SetTickLastCall(function.Deployment, lastSetTicks);
            }
        }

        context.Response.StatusCode = 204;
    }

    private static async Task SendRequest(string queryString, ISendClient sendClient, CustomRequest customRequest, string baseUrl, ILogger<SlimProxyMiddleware> logger, string eventName, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration)
    {
        try
        {
            using HttpResponseMessage responseMessage = await sendClient.SendHttpRequestAsync(customRequest, slimFaasDefaultConfiguration, baseUrl);
            logger.LogDebug(
                "Response from event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent} is {StatusCode}",
                eventName, customRequest.FunctionName, baseUrl, customRequest.Path, queryString,
                responseMessage.StatusCode);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error in sending event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent}",
                eventName, customRequest.FunctionName, baseUrl, customRequest.Path, queryString);
        }
    }

    private async Task BuildSyncResponseAsync(ILogger<SlimProxyMiddleware> logger, HttpContext context, HistoryHttpMemoryService historyHttpService,
        ISendClient sendClient, IReplicasService replicasService, IJobService jobService, string functionName, string functionPath)
    {
        DeploymentInformation? function = SearchFunction(replicasService, functionName);
        if (function == null)
        {
            logger.LogDebug("{FunctionName} not found 404", functionName);
            context.Response.StatusCode = 404;
            return;
        }

        var visibility = GetFunctionVisibility(logger, function, functionPath);

        if (visibility == FunctionVisibility.Private && !MessageComeFromNamespaceInternal(logger, context, replicasService, jobService, function))
        {
            logger.LogDebug("{FunctionName} not found 404 because is private 404", functionName);
            context.Response.StatusCode = 404;
            return;
        }

        await WaitForAnyPodStartedAsync(logger, context, historyHttpService, replicasService, functionName);

        Task<HttpResponseMessage> responseMessagePromise = sendClient.SendHttpRequestSync(context, functionName,
            functionPath, context.Request.QueryString.ToUriComponent(), function.Configuration.DefaultSync, null, new Proxy(replicasService, functionName));

        long lastSetTicks = DateTime.UtcNow.Ticks;
        historyHttpService.SetTickLastCall(functionName, lastSetTicks);
        while (!responseMessagePromise.IsCompleted)
        {
            await Task.Delay(10, context.RequestAborted);
            bool isOneSecondElapsed = new DateTime(lastSetTicks, DateTimeKind.Utc) < DateTime.UtcNow.AddSeconds(-1);
            if (!isOneSecondElapsed)
            {
                continue;
            }

            lastSetTicks = DateTime.UtcNow.Ticks;
            historyHttpService.SetTickLastCall(functionName, lastSetTicks);
        }

        historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
        using HttpResponseMessage responseMessage = responseMessagePromise.Result;
        context.Response.StatusCode = (int)responseMessage.StatusCode;
        CopyFromTargetResponseHeaders(context, responseMessage);
        await responseMessage.Content.CopyToAsync(context.Response.Body);
    }

    private async Task WaitForAnyPodStartedAsync(ILogger<SlimProxyMiddleware> logger, HttpContext context, HistoryHttpMemoryService historyHttpService,
        IReplicasService replicasService, string functionName)
    {
        int numberLoop = _timeoutMaximumWaitWakeSyncFunctionMilliSecond / 10;
        long lastSetTicks = DateTime.UtcNow.Ticks;
        historyHttpService.SetTickLastCall(functionName, lastSetTicks);
        while (numberLoop > 0)
        {
            DeploymentInformation? function = SearchFunction(replicasService, functionName);
            if(function == null)
            {
                continue;
            }
            bool? isAnyContainerStarted = function.Pods.Any(p => p.Ready.HasValue && p.Ready.Value);
            bool isReady = isAnyContainerStarted.Value && function?.EndpointReady == true;
            if (!isReady && !context.RequestAborted.IsCancellationRequested)
            {
                numberLoop--;
                await Task.Delay(10, context.RequestAborted);
                bool isOneSecondElapsed = new DateTime(lastSetTicks) < DateTime.UtcNow.AddSeconds(-1);
                if (isOneSecondElapsed)
                {
                    lastSetTicks = DateTime.UtcNow.Ticks;
                    historyHttpService.SetTickLastCall(functionName, lastSetTicks);
                }
                continue;
            }

            numberLoop = 0;
        }
    }

    private void CopyFromTargetResponseHeaders(HttpContext context, HttpResponseMessage responseMessage)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in responseMessage.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in responseMessage.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        context.Response.Headers.Remove("transfer-encoding");
    }

    private static async Task<CustomRequest> InitCustomRequest(HttpContext context, HttpRequest contextRequest,
        string functionName, string functionPath)
    {
        List<CustomHeader> customHeaders = contextRequest.Headers
            .Select(headers => new CustomHeader(headers.Key, headers.Value.ToArray())).ToList();

        string requestMethod = contextRequest.Method;
        byte[]? requestBodyBytes = null;
        if (!HttpMethods.IsGet(requestMethod) &&
            !HttpMethods.IsHead(requestMethod) &&
            !HttpMethods.IsDelete(requestMethod) &&
            !HttpMethods.IsTrace(requestMethod))
        {
            using StreamContent streamContent = new StreamContent(context.Request.Body);
            using MemoryStream memoryStream = new MemoryStream();
            await streamContent.CopyToAsync(memoryStream);
            requestBodyBytes = memoryStream.ToArray();
        }

        QueryString requestQueryString = contextRequest.QueryString;
        CustomRequest customRequest = new CustomRequest
        {
            Headers = customHeaders,
            FunctionName = functionName,
            Path = functionPath,
            Body = requestBodyBytes,
            Query = requestQueryString.ToUriComponent(),
            Method = requestMethod
        };
        return customRequest;
    }

    private static FunctionInfo GetFunctionInfo(ILogger<SlimProxyMiddleware> faasLogger, HttpRequest contextRequest)
    {
        string requestMethod = contextRequest.Method;
        PathString requestPath = contextRequest.Path;
        QueryString requestQueryString = contextRequest.QueryString;
        string functionBeginPath = FunctionBeginPath(requestPath);
        if (string.IsNullOrEmpty(functionBeginPath))
        {
            return new FunctionInfo(string.Empty, string.Empty);
        }

        string pathString = requestPath.ToUriComponent();
        string[] paths = pathString.Split("/");
        if (paths.Length <= 2)
        {
            return new FunctionInfo(string.Empty, string.Empty);
        }

        string functionName = paths[2];
        string functionPath = pathString.Replace($"{functionBeginPath}/{functionName}", "");
        faasLogger.LogDebug("{Method}: {Function}{UriComponent}", requestMethod, pathString,
            requestQueryString.ToUriComponent());

        FunctionType functionType = functionBeginPath switch
        {
            AsyncFunction => FunctionType.Async,
            Function => FunctionType.Sync,
            StatusFunction => FunctionType.Status,
            WakeFunction => FunctionType.Wake,
            PublishEvent => FunctionType.Publish,
            Job => FunctionType.Job,
            Queue => FunctionType.Queue,
            _ => FunctionType.NotAFunction
        };
        return new FunctionInfo(functionPath, functionName, functionType);
    }

    private static string FunctionBeginPath(PathString path)
    {
        string functionBeginPath = string.Empty;
        if (path.StartsWithSegments(AsyncFunction))
        {
            functionBeginPath = $"{AsyncFunction}";
        }
        else if (path.StartsWithSegments(Function))
        {
            functionBeginPath = $"{Function}";
        }
        else if (path.StartsWithSegments(WakeFunction))
        {
            functionBeginPath = $"{WakeFunction}";
        }
        else if (path.StartsWithSegments(StatusFunction))
        {
            functionBeginPath = $"{StatusFunction}";
        }
        else if (path.StartsWithSegments(PublishEvent))
        {
            functionBeginPath = $"{PublishEvent}";
        }
        else if (path.StartsWithSegments(Job))
        {
            functionBeginPath = $"{Job}";
        }

        return functionBeginPath;
    }

    private record FunctionInfo(string FunctionPath, string FunctionName,
        FunctionType FunctionType = FunctionType.NotAFunction);
}
