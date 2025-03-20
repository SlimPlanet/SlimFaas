using Microsoft.Extensions.Primitives;
using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface ISendClient
{
    Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, Proxy? proxy = null);

    Task<HttpResponseMessage> SendHttpRequestSync(HttpContext httpContext, string functionName, string functionPath,
        string functionQuery, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, Proxy? proxy = null);
}

public class SendClient(HttpClient httpClient, ILogger<SendClient> logger) : ISendClient
{
    private readonly string _baseFunctionUrl =
        Environment.GetEnvironmentVariable(EnvironmentVariables.BaseFunctionUrl) ??
        EnvironmentVariables.BaseFunctionUrlDefault;
    private readonly string _namespaceSlimFaas =
        Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ?? EnvironmentVariables.NamespaceDefault;

    public async Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest,
        SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, Proxy? proxy = null)
    {
        try
        {
            string functionUrl = baseUrl ?? _baseFunctionUrl;
            string customRequestFunctionName = customRequest.FunctionName;
            string customRequestPath = customRequest.Path;
            string customRequestQuery = customRequest.Query;

            httpClient.Timeout = TimeSpan.FromSeconds(slimFaasDefaultConfiguration.HttpTimeout);
            return await Retry.DoRequestAsync(() =>
                    {
                        var promise =
                            ComputeTargetUrlAsync(functionUrl, customRequestFunctionName, customRequestPath, customRequestQuery, _namespaceSlimFaas, proxy);
                        string targetUrl = promise.Result;
                        logger.LogDebug("Sending async request to {TargetUrl}", targetUrl);
                        HttpRequestMessage targetRequestMessage = CreateTargetMessage(customRequest, new Uri(targetUrl));
                        return httpClient.SendAsync(targetRequestMessage,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken?.Token ?? CancellationToken.None);
                    },
                    logger, slimFaasDefaultConfiguration.TimeoutRetries, slimFaasDefaultConfiguration.HttpStatusRetries)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error in SendHttpRequestAsync to {FunctionName} to {FunctionPath} ", customRequest.FunctionName, customRequest.Path);
            throw;
        }
    }

    public async Task<HttpResponseMessage> SendHttpRequestSync(HttpContext context,
        string functionName,
        string functionPath,
        string functionQuery,
        SlimFaasDefaultConfiguration slimFaasDefaultConfiguration,
        string? baseUrl = null,
        Proxy? proxy = null)
    {
        try
        {
            logger.LogDebug("Start sending sync request to {functionName}{functionPath}{functionQuery}", functionName, functionPath ,functionQuery);
            httpClient.Timeout = TimeSpan.FromSeconds(slimFaasDefaultConfiguration.HttpTimeout);
            HttpResponseMessage responseMessage = await  Retry.DoRequestAsync(() =>
                {
                    var promise = ComputeTargetUrlAsync(baseUrl ?? _baseFunctionUrl, functionName, functionPath, functionQuery, _namespaceSlimFaas, proxy);
                    string targetUrl = promise.Result;
                    logger.LogDebug("Sending sync request to {TargetUrl}", targetUrl);
                    HttpRequestMessage targetRequestMessage = CreateTargetMessage(context, new Uri(targetUrl));
                    return httpClient.SendAsync(targetRequestMessage,
                        HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                },
                logger, slimFaasDefaultConfiguration.TimeoutRetries, slimFaasDefaultConfiguration.HttpStatusRetries).ConfigureAwait(false);
            return responseMessage;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error in SendHttpRequestSync to {FunctionName} to {FunctionPath} ", functionName, functionPath);
            throw;
        }
    }

    private void CopyFromOriginalRequestContentAndHeaders(CustomRequest context, HttpRequestMessage requestMessage)
    {
        string requestMethod = context.Method;

        if (!HttpMethods.IsGet(requestMethod) &&
            !HttpMethods.IsHead(requestMethod) &&
            !HttpMethods.IsDelete(requestMethod) &&
            !HttpMethods.IsTrace(requestMethod) &&
            context.Body != null)
        {
            StreamContent streamContent = new StreamContent(new MemoryStream(context.Body));
            requestMessage.Content = streamContent;
        }

        foreach (CustomHeader header in context.Headers)
        {
            requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Values);
        }
    }

    private HttpRequestMessage CreateTargetMessage(CustomRequest context, Uri targetUri)
    {
        HttpRequestMessage requestMessage = new();
        CopyFromOriginalRequestContentAndHeaders(context, requestMessage);

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

    private static async Task<string> ComputeTargetUrlAsync(string functionUrl, string customRequestFunctionName,
        string customRequestPath,
        string customRequestQuery, string namespaceSlimFaas, Proxy? proxy = null)
    {
        if (functionUrl.Contains("{pod_ip}") && proxy != null)
        {
           var ip = proxy.GetNextIP();
           var ports = proxy.GetPorts();
           var count = 300;
           while((ports == null || ports.Count == 0 || string.IsNullOrEmpty(ip))  && count > 0)
           {
               ip = proxy.GetNextIP();
               ports = proxy.GetPorts();
               count--;
               await Task.Delay(100);
           }

           if(ports == null || string.IsNullOrEmpty(ip) || ports.Count == 0)
           {
               throw new Exception("Not port or IP available");
           }

           string url = functionUrl.Replace("{pod_ip}", ip) + customRequestPath + customRequestQuery;
           if (ports is { Count: > 0 })
           {
               url = url.Replace("{pod_port}", ports[0].ToString());
               foreach (int port in ports)
               {
                   var index = ports.IndexOf(port);
                   url = url.Replace($"{{pod_port_{index}}}", port.ToString());
               }
           }
           else
           {
               Console.WriteLine("No ports available");
           }

           return url;
        }

        return functionUrl.Replace("{function_name}", customRequestFunctionName).Replace("{namespace}", namespaceSlimFaas) + customRequestPath +
               customRequestQuery;
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
