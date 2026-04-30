using MemoryPack;
using SlimData.ClusterFiles;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

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
            if (GetPathWithoutPrefix(path,"/").ToLowerInvariant().StartsWith(GetPathWithoutPrefix(pathStartWith.Path, "/")))
            {
                return pathStartWith.Visibility;
            }
            logger.LogWarning("PathStartWithVisibility {PathStartWith} should be prefixed by Public: or Private:", pathStartWith);
        }
        return function.Visibility;
    }

    private static string GetPathWithoutPrefix(string functionPath, string prefix)
    {
        if (functionPath.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
        {
            return functionPath[prefix.Length..];
        }
        return functionPath;
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

        podIps.AddRange(replicasService.Deployments.SlimFaas.Pods.Select(p => p.Ip));

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
        string functionPath,
        long bodyOffloadThresholdBytes = 0,
        IClusterFileSync? fileSync = null,
        IDatabaseService? db = null,
        CancellationToken ct = default)
    {
        List<CustomHeader> customHeaders = contextRequest.Headers
            .Select(headers => new CustomHeader(headers.Key, headers.Value.ToArray())).ToList();

        string requestMethod = contextRequest.Method;
        byte[]? requestBodyBytes = null;
        string? offloadedFileId = null;

        bool hasBody = !HttpMethods.IsGet(requestMethod) &&
                       !HttpMethods.IsHead(requestMethod) &&
                       !HttpMethods.IsDelete(requestMethod) &&
                       !HttpMethods.IsTrace(requestMethod);

        if (hasBody)
        {
            bool shouldOffload = bodyOffloadThresholdBytes > 0
                && fileSync != null
                && db != null
                && (contextRequest.ContentLength == null
                    || contextRequest.ContentLength > bodyOffloadThresholdBytes);

            if (shouldOffload)
            {
                offloadedFileId = Guid.NewGuid().ToString("N");
                var contentType = contextRequest.ContentType ?? "application/octet-stream";
                var contentLength = contextRequest.ContentLength ?? 20L * 1024L * 1024L;

                var put = await fileSync!.BroadcastFilePutAsync(
                    id: offloadedFileId,
                    content: contextRequest.Body,
                    contentType: contentType,
                    contentLengthBytes: contentLength,
                    overwrite: false,
                    ttl: null,
                    ct: ct);

                var meta = new DataSetMetadata(
                    Sha256Hex: put.Sha256Hex,
                    Length: put.Length,
                    ContentType: put.ContentType,
                    FileName: offloadedFileId);
                var metaKey = $"data:file:{offloadedFileId}:meta";
                var metaBytes = MemoryPackSerializer.Serialize(meta);
                await db!.SetAsync(metaKey, metaBytes);
            }
            else
            {
                using StreamContent streamContent = new(context.Request.Body);
                using MemoryStream memoryStream = new();
                await streamContent.CopyToAsync(memoryStream, ct);
                requestBodyBytes = memoryStream.ToArray();
            }
        }

        return new CustomRequest
        {
            Headers = customHeaders,
            FunctionName = functionName,
            Path = functionPath,
            Body = requestBodyBytes,
            Query = contextRequest.QueryString.ToUriComponent(),
            Method = requestMethod,
            OffloadedFileId = offloadedFileId
        };
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

    public static FunctionStatusDetailed MapToFunctionStatusDetailed(DeploymentInformation f)
    {
        int numberReady = f.Pods.Count(p => p.Ready.HasValue && p.Ready.Value);

        var pods = f.Pods.Select(p =>
        {
            string status;
            if (p.Ready is true)
                status = "Running";
            else if (!string.IsNullOrEmpty(p.StartFailureReason))
                status = p.StartFailureReason;
            else if (!string.IsNullOrEmpty(p.AppFailureReason))
                status = p.AppFailureReason;
            else if (p.Started is true)
                status = "Starting";
            else
                status = "Pending";

            return new PodStatus(p.Name, status, p.Ready is true, p.Ip);
        }).ToList();

        return new FunctionStatusDetailed(
            Name: f.Deployment,
            NumberReady: numberReady,
            NumberRequested: f.Replicas,
            PodType: f.PodType.ToString(),
            Visibility: f.Visibility.ToString(),
            Trust: f.Trust.ToString(),
            ReplicasMin: f.ReplicasMin,
            ReplicasAtStart: f.ReplicasAtStart,
            TimeoutSecondBeforeSetReplicasMin: f.TimeoutSecondBeforeSetReplicasMin,
            NumberParallelRequest: f.NumberParallelRequest,
            NumberParallelRequestPerPod: f.NumberParallelRequestPerPod,
            Resources: f.Resources,
            Schedule: f.Schedule,
            Scale: f.Scale,
            SubscribeEvents: f.SubscribeEvents,
            PathsStartWithVisibility: f.PathsStartWithVisibility,
            DependsOn: f.DependsOn,
            Pods: pods
        );
    }
}


