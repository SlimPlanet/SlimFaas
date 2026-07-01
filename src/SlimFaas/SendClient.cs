using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

public interface ISendClient
{
    Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, IProxy? proxy = null, string? reservedPodIp = null, string? activitySource = null, string? activitySourcePod = null, Stream? bodyOverrideStream = null);

    Task<HttpResponseMessage> SendHttpRequestSync(HttpContext httpContext, string functionName, string functionPath,
        string functionQuery, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, IProxy? proxy = null, string? activitySource = null, string? activitySourcePod = null);
}

public class SendClient(HttpClient httpClient, ILogger<SendClient> logger, IOptions<SlimFaasOptions> slimFaasOptions, INamespaceProvider namespaceProvider, NetworkActivityTracker activityTracker) : ISendClient
{
    private readonly string _baseFunctionUrl = slimFaasOptions.Value.BaseFunctionUrl;
    private readonly string _namespaceSlimFaas = namespaceProvider.CurrentNamespace;

    public async Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest,
        SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, IProxy? proxy = null, string? reservedPodIp = null, string? activitySource = null, string? activitySourcePod = null, Stream? bodyOverrideStream = null)
    {
        string source = string.IsNullOrWhiteSpace(activitySource)
            ? NetworkActivityTracker.Actors.SlimFaas
            : activitySource;
        var requestOutId = activityTracker.Record(NetworkActivityTracker.EventTypes.RequestOut, source, customRequest.FunctionName,
            sourcePod: activitySourcePod, targetPod: reservedPodIp);

        try
        {
            string functionUrl = baseUrl ?? _baseFunctionUrl;
            string customRequestFunctionName = customRequest.FunctionName;
            string customRequestPath = customRequest.Path;
            string customRequestQuery = customRequest.Query;
            logger.LogDebug("Start sending sync request to {FunctionName}{FunctionPath}{FunctionQuery}", customRequestFunctionName, customRequestPath ,customRequestQuery);

            using var localCancellationToken = new CancellationTokenSource(
                TimeSpan.FromSeconds(slimFaasDefaultConfiguration.HttpTimeout));

            using var linkedTokenSource = cancellationToken is not null ? CancellationTokenSource.CreateLinkedTokenSource(localCancellationToken.Token, cancellationToken.Token) : null;
            var finalToken = linkedTokenSource?.Token ?? localCancellationToken.Token;

            return await Retry.DoRequestAsync(async () =>
                    {
                        string targetUrl = await ComputeTargetUrlAsync(functionUrl, customRequestFunctionName, customRequestPath, customRequestQuery, _namespaceSlimFaas, proxy, reservedPodIp);
                        logger.LogDebug("Sending async request to {TargetUrl}", targetUrl);
                        HttpRequestMessage targetRequestMessage = CreateTargetMessage(customRequest, new Uri(targetUrl), bodyOverrideStream);
                        return await httpClient.SendAsync(targetRequestMessage,
                            HttpCompletionOption.ResponseHeadersRead,
                            finalToken);
                    },
                    logger, slimFaasDefaultConfiguration.TimeoutRetries, slimFaasDefaultConfiguration.HttpStatusRetries)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error in SendHttpRequestAsync to {FunctionName} to {FunctionPath} ", customRequest.FunctionName, customRequest.Path);
            throw;
        }
        finally
        {
            activityTracker.Record(NetworkActivityTracker.EventTypes.RequestEnd, source, customRequest.FunctionName,
                sourcePod: activitySourcePod, targetPod: reservedPodIp, correlationId: requestOutId);
        }
    }

    public async Task<HttpResponseMessage> SendHttpRequestSync(
        HttpContext httpContext,
        string functionName,
        string functionPath,
        string functionQuery,
        SlimFaasDefaultConfiguration slimFaasDefaultConfiguration,
        string? baseUrl = null,
        IProxy? proxy = null,
        string? activitySource = null,
        string? activitySourcePod = null)
    {
        string source = string.IsNullOrWhiteSpace(activitySource)
            ? NetworkActivityTracker.Actors.SlimFaas
            : activitySource;

        string? reservedSyncIp = null;
        string? requestOutId = null;
        var releaseReservedSyncIpOnError = true;

        try
        {
            logger.LogDebug("Start sending sync request to {FunctionName}{FunctionPath}{FunctionQuery}",
                functionName, functionPath, functionQuery);

            using var localCancellationToken = new CancellationTokenSource(
                TimeSpan.FromSeconds(slimFaasDefaultConfiguration.HttpTimeout));

            var cancellationToken = httpContext.RequestAborted;
            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                localCancellationToken.Token, cancellationToken);

            var finalToken = linkedTokenSource.Token;

            string functionUrl = baseUrl ?? _baseFunctionUrl;
            string targetUrl;
            if (functionUrl.Contains("{pod_ip}") && proxy != null)
            {
                const int maxAttempts = 10;
                IList<int>? ports = null;

                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    reservedSyncIp = proxy.AcquireNextIPForSync();
                    ports = proxy.GetPorts(reservedSyncIp);
                    if (!string.IsNullOrWhiteSpace(reservedSyncIp) && ports is { Count: > 0 })
                    {
                        break;
                    }

                    proxy.ReleaseSyncIP(reservedSyncIp);
                    reservedSyncIp = null;
                    await Task.Delay(100, finalToken);
                }

                if (string.IsNullOrWhiteSpace(reservedSyncIp) || ports is null || ports.Count == 0)
                {
                    throw new Exception("Not port or IP available");
                }

                targetUrl = BuildPodTargetUrl(functionUrl, functionPath + functionQuery, reservedSyncIp, ports);
            }
            else
            {
                targetUrl = await ComputeTargetUrlAsync(
                    functionUrl,
                    functionName,
                    functionPath,
                    functionQuery,
                    _namespaceSlimFaas,
                    proxy);
            }

            requestOutId = activityTracker.Record(NetworkActivityTracker.EventTypes.RequestOut, source, functionName,
                sourcePod: activitySourcePod, targetPod: reservedSyncIp);

            logger.LogDebug("Sending sync request to {TargetUrl}", targetUrl);

            using var targetRequestMessage = CreateTargetMessage(httpContext, new Uri(targetUrl));

            HttpResponseMessage responseMessage = await httpClient.SendAsync(
                targetRequestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                finalToken);

            if (!string.IsNullOrWhiteSpace(reservedSyncIp) && proxy != null)
            {
                releaseReservedSyncIpOnError = false;
                return ReleaseSyncIPWhenResponseIsDisposed(responseMessage, proxy, reservedSyncIp);
            }

            return responseMessage;
        }
        catch (Exception e)
        {
            if (releaseReservedSyncIpOnError)
            {
                proxy?.ReleaseSyncIP(reservedSyncIp);
            }

            logger.LogError(e, "Error in SendHttpRequestSync to {FunctionName} to {FunctionPath} ", functionName, functionPath);
            throw;
        }
        finally
        {
            activityTracker.Record(NetworkActivityTracker.EventTypes.RequestEnd, source, functionName,
                sourcePod: activitySourcePod, targetPod: reservedSyncIp, correlationId: requestOutId);
        }
    }


    private void CopyFromOriginalRequestContentAndHeaders(CustomRequest context, HttpRequestMessage requestMessage, Stream? bodyOverrideStream = null)
    {
        string requestMethod = context.Method;

        if (!HttpMethods.IsGet(requestMethod) &&
            !HttpMethods.IsHead(requestMethod) &&
            !HttpMethods.IsDelete(requestMethod) &&
            !HttpMethods.IsTrace(requestMethod))
        {
            if (bodyOverrideStream != null)
                requestMessage.Content = new StreamContent(bodyOverrideStream);
            else if (context.Body != null)
                requestMessage.Content = new StreamContent(new MemoryStream(context.Body));
        }

        foreach (var header in context.Headers.Where(header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                                                               header.Key.Equals("DPoP", StringComparison.OrdinalIgnoreCase)))
        {
            requestMessage.Headers.Remove(header.Key);
            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Values);
        }

        foreach (CustomHeader header in context.Headers)
        {
            requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Values);
        }
    }

    private HttpRequestMessage CreateTargetMessage(CustomRequest context, Uri targetUri, Stream? bodyOverrideStream = null)
    {
        HttpRequestMessage requestMessage = new();
        CopyFromOriginalRequestContentAndHeaders(context, requestMessage, bodyOverrideStream);

        requestMessage.RequestUri = targetUri;
        requestMessage.Headers.Host = targetUri.Host;
        requestMessage.Method = GetMethod(context.Method);

        return requestMessage;
    }

    private static HttpMethod GetMethod(string method)
    {
        if (HttpMethods.IsDelete(method))
        {
            return HttpMethod.Delete;
        }

        if (HttpMethods.IsGet(method))
        {
            return HttpMethod.Get;
        }

        if (HttpMethods.IsHead(method))
        {
            return HttpMethod.Head;
        }

        if (HttpMethods.IsOptions(method))
        {
            return HttpMethod.Options;
        }

        if (HttpMethods.IsPost(method))
        {
            return HttpMethod.Post;
        }

        if (HttpMethods.IsPut(method))
        {
            return HttpMethod.Put;
        }

        if (HttpMethods.IsTrace(method))
        {
            return HttpMethod.Trace;
        }

        return new HttpMethod(method);
    }

    public static async Task<string> ComputeTargetUrlAsync(string functionUrl, string customRequestFunctionName,
        string customRequestPath,
        string customRequestQuery, string namespaceSlimFaas, IProxy? proxy = null, string? reservedPodIp = null)
    {
        if (functionUrl.Contains("{pod_ip}") && proxy != null)
        {
           var ip = reservedPodIp;
           var ports = proxy.GetPorts(ip);
           var count = 10;
           while ((ports == null || ports.Count == 0 || string.IsNullOrEmpty(ip)) && count > 0)
           {
               ip = proxy.GetNextIP();
               ports = proxy.GetPorts(ip);
               count--;
               await Task.Delay(100);
           }

           if(ports == null || string.IsNullOrEmpty(ip) || ports.Count == 0)
           {
               throw new Exception("Not port or IP available");
           }

           return BuildPodTargetUrl(functionUrl, customRequestPath + customRequestQuery, ip, ports);
        }

        return CombinePaths(functionUrl.Replace("{function_name}", customRequestFunctionName).Replace("{namespace}", namespaceSlimFaas), customRequestPath +
               customRequestQuery);
    }

    public static string CombinePaths(string baseUrl, params string[] segments)
    {
        var builder = new StringBuilder(baseUrl.TrimEnd('/'));

        foreach (var segment in segments)
        {
            if (!string.IsNullOrEmpty(segment))
            {
                var cleanSegment = segment.Trim('/');
                if (!string.IsNullOrEmpty(cleanSegment))
                {
                    builder.Append('/');
                    builder.Append(cleanSegment);
                }
            }
        }

        return builder.ToString();
    }

    private static string BuildPodTargetUrl(string functionUrl, string pathAndQuery, string ip, IList<int> ports)
    {
        string url = CombinePaths(functionUrl.Replace("{pod_ip}", ip), pathAndQuery);
        if (ports is { Count: > 0 })
        {
            url = url.Replace("{pod_port}", ports[0].ToString());
            foreach (int port in ports)
            {
                var index = ports.IndexOf(port);
                url = url.Replace($"{{pod_port_{index}}}", port.ToString());
            }
        }

        return url;
    }

    private static HttpResponseMessage ReleaseSyncIPWhenResponseIsDisposed(HttpResponseMessage responseMessage, IProxy proxy, string reservedSyncIp)
    {
        responseMessage.Content = new ReleaseOnDisposeHttpContent(
            responseMessage.Content,
            () => proxy.ReleaseSyncIP(reservedSyncIp));
        return responseMessage;
    }

    private sealed class ReleaseOnDisposeHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _release;
        private int _released;

        public ReleaseOnDisposeHttpContent(HttpContent inner, Action release)
        {
            _inner = inner;
            _release = release;

            foreach (var header in _inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _inner.CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => _inner.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => _inner.ReadAsStreamAsync(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            if (_inner.Headers.ContentLength.HasValue)
            {
                length = _inner.Headers.ContentLength.Value;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    _release();
                }
            }

            base.Dispose(disposing);
        }
    }


    private HttpRequestMessage CreateTargetMessage(HttpContext context, Uri targetUri)
    {
        HttpRequestMessage requestMessage = new();
        CopyFromOriginalRequestContentAndHeaders(context, requestMessage);

        requestMessage.RequestUri = targetUri;
        foreach (KeyValuePair<string, StringValues> header in context.Request.Headers.Where(h =>
                     h.Key.ToLower() != "host"))
        {
            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        requestMessage.Headers.Host = targetUri.Host;
        requestMessage.Method = GetMethod(context.Request.Method);

        return requestMessage;
    }

    private void CopyFromOriginalRequestContentAndHeaders(HttpContext context, HttpRequestMessage requestMessage)
    {
        string requestMethod = context.Request.Method;
        context.Request.EnableBuffering(bufferThreshold: 1024 * 100, bufferLimit: 200 * 1024 * 1024);

        if (!HttpMethods.IsGet(requestMethod) &&
            !HttpMethods.IsHead(requestMethod) &&
            !HttpMethods.IsDelete(requestMethod) &&
            !HttpMethods.IsTrace(requestMethod))
        {
            StreamContent streamContent = new(context.Request.Body);
            requestMessage.Content = streamContent;
        }

        foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
        {
            requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

}
