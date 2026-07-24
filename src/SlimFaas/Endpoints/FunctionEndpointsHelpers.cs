using System.Buffers;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using SlimData.ClusterFiles;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas;

namespace SlimFaas.Endpoints;

public static class FunctionEndpointsHelpers
{
    private const long DefaultFileOffloadContentLengthBytes = 256L * 1024L * 1024L;
    private const long LengthBytes = 512L * 1024L;

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
        IJobService jobService)
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
        long bodyOffloadThresholdBytes = LengthBytes,
        string queueElementId = "",
        IClusterFileSync? fileSync = null,
        IDatabaseService? db = null,
        long unknownLengthReservationBytes = DefaultFileOffloadContentLengthBytes,
        CancellationToken ct = default)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(FunctionEndpointsHelpers));
        List<CustomHeader> customHeaders = contextRequest.Headers
            .Select(headers => new CustomHeader(headers.Key, headers.Value.ToArray())).ToList();

        string requestMethod = contextRequest.Method;
        byte[]? requestBodyBytes = null;
        string? offloadedFileId = null;

        bool hasBody = !HttpMethods.IsGet(requestMethod) &&
                       !HttpMethods.IsHead(requestMethod) &&
                       !HttpMethods.IsDelete(requestMethod) &&
                       !HttpMethods.IsTrace(requestMethod);

        if (!hasBody)
        {
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

        bool canOffload = bodyOffloadThresholdBytes > 0
                          && fileSync != null
                          && db != null;
        bool shouldOffload = canOffload
                             && contextRequest.ContentLength > bodyOffloadThresholdBytes;
        Stream? offloadContent = shouldOffload ? contextRequest.Body : null;

        if (canOffload && contextRequest.ContentLength is null)
        {
            var bodyProbe = await ReadBodyProbeAsync(
                contextRequest.Body,
                bodyOffloadThresholdBytes,
                ct).ConfigureAwait(false);

            if (bodyProbe.Length <= bodyOffloadThresholdBytes)
            {
                requestBodyBytes = bodyProbe;
            }
            else
            {
                shouldOffload = true;
                offloadContent = new PrefixedReadStream(bodyProbe, contextRequest.Body);
            }
        }

        logger.LogDebug(
            "Request body offload check. ShouldOffload={ShouldOffload} ContentLength={ContentLength} Threshold={Threshold}",
            shouldOffload,
            contextRequest.ContentLength,
            bodyOffloadThresholdBytes);
        if (shouldOffload)
        {
            offloadedFileId = DataFileKeys.CreateInternalOffloadId();
            var contentType = contextRequest.ContentType ?? "application/octet-stream";
            var contentLength = contextRequest.ContentLength ?? unknownLengthReservationBytes;

            var tags = new Dictionary<string, string>
            {
                { "QueueElementId", queueElementId },
                { "FunctionName", functionName }
            };

            var put = await fileSync!.BroadcastFilePutAsync(
                id: offloadedFileId,
                content: offloadContent!,
                contentType: contentType,
                contentLengthBytes: contentLength,
                overwrite: false,
                ttl: null,
                ct: ct,
                tags);

            var meta = new DataSetMetadata(
                Sha256Hex: put.Sha256Hex,
                Length: put.Length,
                ContentType: put.ContentType,
                FileName: offloadedFileId,
                Tags: tags);

            var metaKey = DataFileKeys.MetaKey(offloadedFileId);
            if(logger.IsEnabled(LogLevel.Debug)) {
                logger.LogDebug(
                    "Offloading request metadata. MetaKey={MetaKey} Tags={Tags}",
                    metaKey,
                    string.Join(", ", tags.Select(tag => $"{tag.Key}={tag.Value}")));
            }
            var metaBytes = MemoryPackSerializer.Serialize(meta);
            await db!.SetAsync(metaKey, metaBytes);
        }
        else if (requestBodyBytes is null)
        {
            using StreamContent streamContent = new(context.Request.Body);
            using MemoryStream memoryStream = new();
            await streamContent.CopyToAsync(memoryStream, ct);
            requestBodyBytes = memoryStream.ToArray();
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

    private static async Task<byte[]> ReadBodyProbeAsync(
        Stream body,
        long thresholdBytes,
        CancellationToken ct)
    {
        var probeLength = checked((int)(thresholdBytes + 1));
        using MemoryStream probe = new();
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(64 * 1024, probeLength));
        try
        {
            while (probe.Length < probeLength)
            {
                var remaining = probeLength - (int)probe.Length;
                var read = await body.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                await probe.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return probe.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class PrefixedReadStream(byte[] prefix, Stream tail) : Stream
    {
        private int _prefixOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixOffset < prefix.Length)
            {
                var copied = Math.Min(count, prefix.Length - _prefixOffset);
                prefix.AsSpan(_prefixOffset, copied).CopyTo(buffer.AsSpan(offset, copied));
                _prefixOffset += copied;
                return copied;
            }

            return tail.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_prefixOffset < prefix.Length)
            {
                var copied = Math.Min(buffer.Length, prefix.Length - _prefixOffset);
                prefix.AsMemory(_prefixOffset, copied).CopyTo(buffer);
                _prefixOffset += copied;
                return copied;
            }

            return await tail.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
            Retry: new RetryConfig(
                new RetryConfigEntry(
                    f.Configuration.DefaultAsync.HttpTimeout,
                    f.Configuration.DefaultAsync.TimeoutRetries,
                    f.Configuration.DefaultAsync.HttpStatusRetries),
                new RetryConfigEntry(
                    f.Configuration.DefaultPublish.HttpTimeout,
                    f.Configuration.DefaultPublish.TimeoutRetries,
                    f.Configuration.DefaultPublish.HttpStatusRetries)),
            SubscribeEvents: f.SubscribeEvents,
            PathsStartWithVisibility: f.PathsStartWithVisibility,
            DependsOn: f.DependsOn,
            Pods: pods
        );
    }
}
