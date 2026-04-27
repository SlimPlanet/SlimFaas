﻿using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests;

public class SendClientShould
{
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task CallFunctionAsync(string httpMethod)
    {
        HttpRequestMessage? sendedRequest = null;

        HttpClient httpClient = new HttpClient(new HttpMessageHandlerStub(async (request, cancellationToken) =>
        {
            HttpResponseMessage responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("This is a reply")
            };
            sendedRequest = request;
            return await Task.FromResult(responseMessage);
        }));

        var mockLogger = new Mock<ILogger<SendClient>>();
        var options = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            BaseFunctionUrl = "http://{function_name}:8080/",
            Namespace = "default"
        });
        var namespaceProviderMock = new Mock<INamespaceProvider>();
        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");
        var tracker = new SlimFaas.Endpoints.NetworkActivityTracker();
        SendClient sendClient = new(httpClient, mockLogger.Object, options, namespaceProviderMock.Object, tracker);
        CustomRequest customRequest =
            new CustomRequest(new List<CustomHeader> { new() { Key = "key", Values = new[] { "value1" } } },
                new byte[1], "fibonacci", "health", httpMethod, "");
        HttpResponseMessage response = await sendClient.SendHttpRequestAsync(customRequest, new SlimFaasDefaultConfiguration());

        Uri expectedUri = new Uri("http://fibonacci:8080/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(sendedRequest);
        Assert.Equal(sendedRequest.RequestUri, expectedUri);
        var events = tracker.GetRecent();
        Assert.Equal(2, events.Count);
        Assert.Equal(SlimFaas.Endpoints.NetworkActivityTracker.EventTypes.RequestOut, events[0].Type);
        Assert.Equal(SlimFaas.Endpoints.NetworkActivityTracker.EventTypes.RequestEnd, events[1].Type);
        Assert.Equal(SlimFaas.Endpoints.NetworkActivityTracker.Actors.SlimFaas, events[0].Source);
    }


    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task CallFunctionSync(string httpMethod)
    {
        HttpRequestMessage? sendedRequest = null;
        HttpClient httpClient = new(new HttpMessageHandlerStub(async (request, cancellationToken) =>
        {
            HttpResponseMessage responseMessage = new(HttpStatusCode.OK)
            {
                Content = new StringContent("This is a reply")
            };
            sendedRequest = request;
            return await Task.FromResult(responseMessage);
        }));
        var mockLogger = new Mock<ILogger<SendClient>>();
        var options = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            BaseFunctionUrl = "http://{function_name}:8080/",
            Namespace = "default"
        });
        var namespaceProviderMock = new Mock<INamespaceProvider>();
        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");
        var tracker = new SlimFaas.Endpoints.NetworkActivityTracker();
        SendClient sendClient = new(httpClient, mockLogger.Object, options, namespaceProviderMock.Object, tracker);

        DefaultHttpContext httpContext = new();
        HttpRequest httpContextRequest = httpContext.Request;
        string authorization = "bearer value1";
        httpContextRequest.Headers.Add("Authorization", authorization);
        httpContextRequest.Method = httpMethod;
        httpContextRequest.Path = "/fibonacci/health";
        httpContextRequest.Host = new HostString("fibonacci");
        httpContextRequest.Scheme = "http";
        httpContextRequest.Body = new MemoryStream();
        httpContextRequest.Body.WriteByte(1);
        httpContextRequest.Body.Position = 0;
        httpContextRequest.ContentLength = 1;
        httpContextRequest.ContentType = "application/json";

        SlimFaasDefaultConfiguration slimFaasDefaultConfiguration = new();

        HttpResponseMessage response = await sendClient.SendHttpRequestSync(httpContext, "fibonacci", "health", "", slimFaasDefaultConfiguration);

        Uri expectedUri = new Uri("http://fibonacci:8080/health");
        Assert.NotNull(sendedRequest);
        Assert.Equal(sendedRequest.RequestUri, expectedUri);
        Assert.Equal(authorization, sendedRequest?.Headers?.Authorization?.ToString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = tracker.GetRecent();
        Assert.Equal(2, events.Count);
        Assert.Equal(SlimFaas.Endpoints.NetworkActivityTracker.EventTypes.RequestOut, events[0].Type);
        Assert.Equal(SlimFaas.Endpoints.NetworkActivityTracker.EventTypes.RequestEnd, events[1].Type);
        Assert.Equal(SlimFaas.Endpoints.NetworkActivityTracker.Actors.SlimFaas, events[0].Source);
    }

    [Fact]
    public async Task LoadBalanceSyncRequestsWithoutPerPodLimit()
    {
        List<string> requestedHosts = new();
        HttpClient httpClient = new(new HttpMessageHandlerStub(request =>
        {
            requestedHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("This is a reply")
            });
        }));

        var mockLogger = new Mock<ILogger<SendClient>>();
        var options = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            BaseFunctionUrl = "http://{pod_ip}:{pod_port}/",
            Namespace = "default"
        });
        var namespaceProviderMock = new Mock<INamespaceProvider>();
        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");
        var tracker = new SlimFaas.Endpoints.NetworkActivityTracker();
        SendClient sendClient = new(httpClient, mockLogger.Object, options, namespaceProviderMock.Object, tracker);

        var replicasService = new FakeReplicasService
        {
            Deployments = new FakeDeploymentCollection
            {
                Functions = new List<DeploymentInformation>
                {
                    new(
                        Deployment: "fibonacci",
                        Namespace: "default",
                        Pods: new List<PodInformation>
                        {
                            new("pod1", true, true, "10.0.0.1", "fibonacci", new List<int> { 8080 }),
                            new("pod2", true, true, "10.0.0.2", "fibonacci", new List<int> { 8080 })
                        },
                        Configuration: new SlimFaasConfiguration(),
                        Replicas: 2,
                        EndpointReady: true)
                }
            }
        };
        var proxy = new Proxy(replicasService, "fibonacci");

        HttpResponseMessage? firstResponse = null;
        HttpResponseMessage? secondResponse = null;
        try
        {
            firstResponse = await sendClient.SendHttpRequestSync(
                BuildHttpContext(), "fibonacci", "health", "", new SlimFaasDefaultConfiguration(), proxy: proxy);
            secondResponse = await sendClient.SendHttpRequestSync(
                BuildHttpContext(), "fibonacci", "health", "", new SlimFaasDefaultConfiguration(), proxy: proxy);

            Assert.Equal(2, requestedHosts.Distinct().Count());

            using HttpResponseMessage thirdResponse = await sendClient.SendHttpRequestSync(
                BuildHttpContext(), "fibonacci", "health", "", new SlimFaasDefaultConfiguration(), proxy: proxy);

            Assert.Equal(HttpStatusCode.OK, thirdResponse.StatusCode);
            Assert.Equal(3, requestedHosts.Count);
            Assert.Equal(2, requestedHosts.Distinct().Count());
        }
        finally
        {
            firstResponse?.Dispose();
            secondResponse?.Dispose();
        }
    }

    private static DefaultHttpContext BuildHttpContext()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/fibonacci/health";
        httpContext.Request.Host = new HostString("fibonacci");
        httpContext.Request.Scheme = "http";
        httpContext.Request.Body = new MemoryStream();
        return httpContext;
    }
}

public class HttpMessageHandlerStub : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

    public HttpMessageHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) =>
        _sendAsync = sendAsync;

    public HttpMessageHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) =>
        _sendAsync = (request, _) => sendAsync(request);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) => await _sendAsync(request, cancellationToken);
}
