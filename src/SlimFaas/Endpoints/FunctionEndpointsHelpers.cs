using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SlimData.ClusterFiles;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Local;
using SlimFaas.Options;
using SlimFaas;

namespace SlimFaas.Endpoints;

public static class FunctionEndpointsHelpers
{
    private const long DefaultFileOffloadContentLengthBytes = 256L * 1024L * 1024L;
    private const long LengthBytes = 512L * 1024L;

    internal readonly record struct NetworkActivityCaller(string Actor, string SourcePod);

    public static DeploymentInformation? SearchFunction(IReplicasService replicasService, string functionName)
    {
        return replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == functionName);
    }

    /// <summary>
    /// Resolves the workload responsible for an incoming network activity event.
    /// SlimFaas-created jobs have a stable generated name, so the UI can target the
    /// exact running job node while retaining the configuration name as a fallback.
    /// </summary>
    internal static NetworkActivityCaller ResolveNetworkActivityCaller(
        HttpContext context,
        IJobService jobService,
        string localJobToken = "")
    {
        IList<Kubernetes.Job> jobs = jobService.Jobs ?? Array.Empty<Kubernetes.Job>();
        string localJobName = context.Request.Headers[LocalJobGateway.JobHeaderName]
            .FirstOrDefault()?
            .Trim() ?? string.Empty;
        string suppliedSignature = context.Request.Headers[LocalJobGateway.SignatureHeaderName]
            .FirstOrDefault()?
            .Trim() ?? string.Empty;
        context.Request.Headers.Remove(LocalJobGateway.JobHeaderName);
        context.Request.Headers.Remove(LocalJobGateway.SignatureHeaderName);
        if (!string.IsNullOrEmpty(localJobName) &&
            !string.IsNullOrEmpty(localJobToken) &&
            SignaturesEqual(
                suppliedSignature,
                LocalJobGateway.CreateSignature(localJobName, localJobToken)) &&
            TryCreateJobCaller(localJobName, out NetworkActivityCaller localCaller))
        {
            return localCaller;
        }

        if (jobs.Count == 0)
        {
            string remoteIp = NormalizeNetworkAddress(
                context.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
            return new NetworkActivityCaller(NetworkActivityTracker.Actors.External, remoteIp);
        }

        var candidates = GetCallerIpCandidates(context).ToList();

        foreach (string candidate in candidates)
        {
            foreach (Kubernetes.Job job in jobs.Where(job => job.Status == JobStatus.Running))
            {
                bool matchesJobIp = job.Ips
                    .Where(ip => !string.IsNullOrWhiteSpace(ip))
                    .Select(NormalizeNetworkAddress)
                    .Any(ip => string.Equals(ip, candidate, StringComparison.OrdinalIgnoreCase));
                if (!matchesJobIp)
                {
                    continue;
                }

                if (TryCreateJobCaller(job.Name, out NetworkActivityCaller caller))
                    return caller;
            }
        }

        string fallbackIp = NormalizeNetworkAddress(
            context.Connection.RemoteIpAddress?.ToString()
            ?? candidates.FirstOrDefault()
            ?? string.Empty);
        return new NetworkActivityCaller(NetworkActivityTracker.Actors.External, fallbackIp);
    }

    internal static string GetLocalJobToken(HttpContext context)
        => context.RequestServices
            .GetService<IOptions<SlimFaasOptions>>()?
            .Value
            .Process
            .Token ?? string.Empty;

    private static bool TryCreateJobCaller(
        string jobName,
        out NetworkActivityCaller caller)
    {
        int separatorIndex = jobName.LastIndexOf(
            KubernetesService.SlimfaasJobKey,
            StringComparison.OrdinalIgnoreCase);
        if (separatorIndex > 0)
        {
            caller = new NetworkActivityCaller(
                jobName[..separatorIndex],
                jobName);
            return true;
        }

        caller = default;
        return false;
    }

    private static bool SignaturesEqual(string supplied, string expected)
    {
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static IEnumerable<string> GetCallerIpCandidates(HttpContext context)
    {
        string remoteIp = NormalizeNetworkAddress(context.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
        if (!string.IsNullOrEmpty(remoteIp))
        {
            yield return remoteIp;
        }

        foreach (string? rawHeaderValue in context.Request.Headers["X-Forwarded-For"])
        {
            string rawHeader = rawHeaderValue ?? string.Empty;
            foreach (string part in rawHeader.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate = NormalizeNetworkAddress(part);
                if (!string.IsNullOrEmpty(candidate)
                    && !string.Equals(candidate, remoteIp, StringComparison.OrdinalIgnoreCase))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string NormalizeNetworkAddress(string value)
    {
        string candidate = value.Trim().Trim('"');
        if (string.IsNullOrEmpty(candidate))
        {
            return string.Empty;
        }

        int closingBracket = candidate.IndexOf(']');
        if (candidate[0] == '['
            && closingBracket > 0)
        {
            candidate = candidate[1..closingBracket];
        }
        else if (candidate.Count(c => c == ':') == 1)
        {
            int separatorIndex = candidate.LastIndexOf(':');
            if (separatorIndex > 0
                && int.TryParse(candidate[(separatorIndex + 1)..], out _))
            {
                candidate = candidate[..separatorIndex];
            }
        }

        if (!IPAddress.TryParse(candidate, out IPAddress? address))
        {
            return candidate;
        }

        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();
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
            await db!.SetQueueMetadataAsync(metaKey, metaBytes);
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
