﻿using Microsoft.Extensions.Primitives;
using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface ISendClient
{
    Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null);

    Task<HttpResponseMessage> SendHttpRequestSync(HttpContext httpContext, string functionName, string functionPath,
        string functionQuery, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null);
}

public class SendClient(HttpClient httpClient, ILogger<SendClient> logger) : ISendClient
{
    private readonly string _baseFunctionUrl =
        Environment.GetEnvironmentVariable(EnvironmentVariables.BaseFunctionUrl) ??
        EnvironmentVariables.BaseFunctionUrlDefault;
    private readonly string _namespaceSlimFaas =
        Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ?? EnvironmentVariables.NamespaceDefault;

    public async Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest,
        SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null)
    {
        try
        {
            string functionUrl = baseUrl ?? _baseFunctionUrl;
            string customRequestFunctionName = customRequest.FunctionName;
            string customRequestPath = customRequest.Path;
            string customRequestQuery = customRequest.Query;
            string targetUrl =
                ComputeTargetUrl(functionUrl, customRequestFunctionName, customRequestPath, customRequestQuery, _namespaceSlimFaas);
            logger.LogDebug("Sending async request to {TargetUrl}", targetUrl);


            httpClient.Timeout = TimeSpan.FromSeconds(slimFaasDefaultConfiguration.HttpTimeout);
            return await Retry.DoRequestAsync(() =>
                    {
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

    public async Task<HttpResponseMessage> SendHttpRequestSync(HttpContext context, string functionName,
        string functionPath, string functionQuery, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null)
    {
        try
        {
            string targetUrl = ComputeTargetUrl(baseUrl ?? _baseFunctionUrl, functionName, functionPath, functionQuery, _namespaceSlimFaas);
            logger.LogDebug("Sending sync request to {TargetUrl}", targetUrl);
            httpClient.Timeout = TimeSpan.FromSeconds(slimFaasDefaultConfiguration.HttpTimeout);
            HttpResponseMessage responseMessage = await  Retry.DoRequestAsync(() =>
                {
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

    private static string ComputeTargetUrl(string functionUrl, string customRequestFunctionName,
        string customRequestPath,
        string customRequestQuery, string namespaceSlimFaas )
    {
        string url = functionUrl.Replace("{function_name}", customRequestFunctionName).Replace("{namespace}", namespaceSlimFaas) + customRequestPath +
                     customRequestQuery;
        return url;
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
