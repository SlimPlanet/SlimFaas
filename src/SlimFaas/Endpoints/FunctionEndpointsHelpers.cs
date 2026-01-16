using System.Collections.Immutable;
using System.Net;
using MemoryPack;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Security;

namespace SlimFaas.Endpoints;

public static class FunctionEndpointsHelpers
{
    public static DeploymentInformation? SearchFunction(IReplicasService replicasService, string functionName)
    {
        return replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == functionName);
    }

    public static FunctionVisibility GetFunctionVisibility(ILogger logger, DeploymentInformation function, string path)
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

    public static bool MessageComeFromNamespaceInternal(
        ILogger logger,
        HttpContext context,
        IReplicasService replicasService,
        IJobService jobService,
        DeploymentInformation? currentFunction = null)
    {
        List<string> podIps = replicasService.Deployments.Functions
            .Where(f => f.Trust == FunctionTrust.Trusted)
            .SelectMany(p => p.Pods)
            .Select(p => p.Ip)
            .ToList();

        podIps.AddRange(jobService.Jobs.SelectMany(job => job.Ips));

        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "";
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "";

        logger.LogDebug("ForwardedFor: {ForwardedFor}, RemoteIp: {RemoteIp}", forwardedFor, remoteIp);

        if (logger.IsEnabled(LogLevel.Debug))
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

    public static async Task<CustomRequest> InitCustomRequest(
        HttpContext context,
        HttpRequest contextRequest,
        string functionName,
        string functionPath)
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
            using StreamContent streamContent = new(context.Request.Body);
            using MemoryStream memoryStream = new();
            await streamContent.CopyToAsync(memoryStream);
            requestBodyBytes = memoryStream.ToArray();
        }

        QueryString requestQueryString = contextRequest.QueryString;
        CustomRequest customRequest = new()
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

    public static FunctionStatus MapToFunctionStatus(DeploymentInformation functionDeploymentInformation)
    {
        int numberReady = functionDeploymentInformation.Pods.Count(p => p.Ready.HasValue && p.Ready.Value);
        int numberRequested = functionDeploymentInformation.Replicas;

        return new FunctionStatus(
            numberReady,
            numberRequested,
            functionDeploymentInformation.PodType.ToString(),
            functionDeploymentInformation.Visibility.ToString(),
            functionDeploymentInformation.Deployment);
    }
}


