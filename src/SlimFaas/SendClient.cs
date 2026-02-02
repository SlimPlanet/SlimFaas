﻿using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

public interface ISendClient
{
    Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, Proxy? proxy = null);

    Task<HttpResponseMessage> SendHttpRequestSync(HttpContext httpContext, string functionName, string functionPath,
        string functionQuery, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, Proxy? proxy = null);
}

public class SendClient(HttpClient httpClient, ILogger<SendClient> logger, IOptions<SlimFaasOptions> slimFaasOptions) : ISendClient
{
    private readonly string _baseFunctionUrl = slimFaasOptions.Value.BaseFunctionUrl;
    private readonly string _namespaceSlimFaas = slimFaasOptions.Value.Namespace;

    public async Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest,
        SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, Proxy? proxy = null)
    {
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
                        string targetUrl = await ComputeTargetUrlAsync(functionUrl, customRequestFunctionName, customRequestPath, customRequestQuery, _namespaceSlimFaas, proxy);
                        logger.LogDebug("Sending async request to {TargetUrl}", targetUrl);
                        HttpRequestMessage targetRequestMessage = CreateTargetMessage(customRequest, new Uri(targetUrl));
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
    }

public async Task<HttpResponseMessage> SendHttpRequestSync(
    HttpContext httpContext,
    string functionName,
    string functionPath,
    string functionQuery,
    SlimFaasDefaultConfiguration slimFaasDefaultConfiguration,
    string? baseUrl = null,
    Proxy? proxy = null)
{
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

        //HttpResponseMessage responseMessage = await Retry.DoRequestAsync(async () =>
           // {

                string targetUrl = await ComputeTargetUrlAsync(
                    baseUrl ?? _baseFunctionUrl,
                    functionName,
                    functionPath,
                    functionQuery,
                    _namespaceSlimFaas,
                    proxy);

                logger.LogDebug("Sending sync request to {TargetUrl}", targetUrl);

                using var targetRequestMessage = CreateTargetMessage(httpContext, new Uri(targetUrl));

                return await httpClient.SendAsync(
                    targetRequestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    finalToken);
           // },
           // logger,
           // slimFaasDefaultConfiguration.TimeoutRetries,
           // slimFaasDefaultConfiguration.HttpStatusRetries).ConfigureAwait(false);

       // return responseMessage;
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
            StreamContent streamContent = new(new MemoryStream(context.Body));
            requestMessage.Content = streamContent;
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

    public static async Task<string> ComputeTargetUrlAsync(string functionUrl, string customRequestFunctionName,
        string customRequestPath,
        string customRequestQuery, string namespaceSlimFaas, IProxy? proxy = null)
    {
        if (functionUrl.Contains("{pod_ip}") && proxy != null)
        {
           var ip = proxy.GetNextIP();
           var ports = proxy.GetPorts();
           var count = 10;
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

           string url = CombinePaths(functionUrl.Replace("{pod_ip}", ip), customRequestPath + customRequestQuery);
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
