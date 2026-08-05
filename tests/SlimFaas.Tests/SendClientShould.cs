﻿using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests;

public class SendClientShould
{
    [Fact]
    public async Task Async_request_retries_seekable_override_without_disposing_owned_stream()
    {
        var attempts = new List<byte[]>();
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new HttpMessageHandlerStub(async (request, cancellationToken) =>
        {
            requests.Add(request);
            attempts.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(
                attempts.Count == 1
                    ? HttpStatusCode.InternalServerError
                    : HttpStatusCode.OK);
        }));
        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(n => n.CurrentNamespace).Returns("default");
        var sendClient = new SendClient(
            httpClient,
            new Mock<ILogger<SendClient>>().Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                BaseFunctionUrl = "http://{function_name}:8080/",
                Namespace = "default"
            }),
            namespaceProvider.Object,
            new SlimFaas.Endpoints.NetworkActivityTracker());
        var body = new MemoryStream([1, 2, 3]);
        var request = new CustomRequest(
            [],
            Body: null,
            FunctionName: "worker",
            Path: "/run",
            Method: "POST",
            Query: "");

        using var response = await sendClient.SendHttpRequestAsync(
            request,
            new SlimFaasDefaultConfiguration
            {
                TimeoutRetries = [0],
                HttpStatusRetries = [(int)HttpStatusCode.InternalServerError]
            },
            bodyOverrideStream: body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal(new byte[] { 1, 2, 3 }, attempt));
        Assert.True(body.CanRead);
        Assert.All(
            requests,
            sent => Assert.Throws<ObjectDisposedException>(
                () => sent.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult()));
        body.Dispose();
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

        SlimFaasSyncConfiguration slimFaasSyncConfiguration = new();

        HttpResponseMessage response = await sendClient.SendHttpRequestSync(httpContext, "fibonacci", "health", "", slimFaasSyncConfiguration);

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
                BuildHttpContext(), "fibonacci", "health", "", new SlimFaasSyncConfiguration(), proxy: proxy);
            secondResponse = await sendClient.SendHttpRequestSync(
                BuildHttpContext(), "fibonacci", "health", "", new SlimFaasSyncConfiguration(), proxy: proxy);

            Assert.Equal(2, requestedHosts.Distinct().Count());

            using HttpResponseMessage thirdResponse = await sendClient.SendHttpRequestSync(
                BuildHttpContext(), "fibonacci", "health", "", new SlimFaasSyncConfiguration(), proxy: proxy);

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

    [Fact]
    public async Task LoadBalanceLocalReplicasByRoutingKeyAndPort()
    {
        var requestedPorts = new List<int>();
        using var httpClient = new HttpClient(new HttpMessageHandlerStub(request =>
        {
            requestedPorts.Add(request.RequestUri!.Port);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("OK")
            });
        }));
        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(provider => provider.CurrentNamespace).Returns("default");
        var tracker = new SlimFaas.Endpoints.NetworkActivityTracker();
        var sendClient = new SendClient(
            httpClient,
            new Mock<ILogger<SendClient>>().Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                BaseFunctionUrl = "http://{pod_ip}:{pod_port}/",
                Namespace = "default"
            }),
            namespaceProvider.Object,
            tracker);
        var replicasService = new FakeReplicasService
        {
            Deployments = new FakeDeploymentCollection
            {
                Functions =
                [
                    new DeploymentInformation(
                        Deployment: "local-fibonacci",
                        Namespace: "default",
                        Pods:
                        [
                            new PodInformation(
                                "local-fibonacci-0",
                                true,
                                true,
                                "127.0.0.1",
                                "local-fibonacci",
                                [5001])
                            {
                                RoutingKey = "local-fibonacci-0"
                            },
                            new PodInformation(
                                "local-fibonacci-1",
                                true,
                                true,
                                "127.0.0.1",
                                "local-fibonacci",
                                [5002])
                            {
                                RoutingKey = "local-fibonacci-1"
                            }
                        ],
                        Configuration: new SlimFaasConfiguration(),
                        Replicas: 2,
                        EndpointReady: true)
                ]
            }
        };
        var proxy = new Proxy(replicasService, "local-fibonacci");

        HttpResponseMessage? firstResponse = null;
        HttpResponseMessage? secondResponse = null;
        try
        {
            firstResponse = await sendClient.SendHttpRequestSync(
                BuildHttpContext(),
                "local-fibonacci",
                "health",
                "",
                new SlimFaasSyncConfiguration(),
                proxy: proxy);
            secondResponse = await sendClient.SendHttpRequestSync(
                BuildHttpContext(),
                "local-fibonacci",
                "health",
                "",
                new SlimFaasSyncConfiguration(),
                proxy: proxy);

            Assert.Equal([5001, 5002], requestedPorts.Order().ToArray());
            Assert.Equal(
                new string?[] { "local-fibonacci-0", "local-fibonacci-1" },
                tracker.GetRecent()
                    .Where(item => item.Type == SlimFaas.Endpoints.NetworkActivityTracker.EventTypes.RequestOut)
                    .Select(item => item.TargetPod)
                    .Order()
                    .ToArray());
        }
        finally
        {
            firstResponse?.Dispose();
            secondResponse?.Dispose();
        }
    }

    [Fact]
    public async Task RouteSyncAndQueuedAsyncRequestsThroughExplicitDebugEndpoint()
    {
        var captured = new List<(Uri Uri, string Method, string Body, string? DebugHeader)>();
        using var httpClient = new HttpClient(new HttpMessageHandlerStub(
            async (request, cancellationToken) =>
            {
                string body = request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                string? debugHeader = request.Headers.TryGetValues("X-Debug", out IEnumerable<string>? values)
                    ? Assert.Single(values)
                    : request.Content?.Headers.TryGetValues("X-Debug", out values) == true
                        ? Assert.Single(values)
                        : null;
                captured.Add((request.RequestUri!, request.Method.Method, body, debugHeader));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("OK")
                };
            }));
        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(provider => provider.CurrentNamespace).Returns("default");
        var sendClient = new SendClient(
            httpClient,
            new Mock<ILogger<SendClient>>().Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                BaseFunctionUrl = "http://{pod_ip}:{pod_port}/",
                Namespace = "default"
            }),
            namespaceProvider.Object,
            new SlimFaas.Endpoints.NetworkActivityTracker());
        var replicasService = new FakeReplicasService
        {
            Deployments = new FakeDeploymentCollection
            {
                Functions =
                [
                    new DeploymentInformation(
                        Deployment: "debug-function",
                        Namespace: "default",
                        Pods:
                        [
                            new PodInformation(
                                "debug-function-debug",
                                true,
                                true,
                                "10.0.0.99",
                                "debug-function",
                                [8080])
                            {
                                RoutingKey = "debug-function-debug",
                                EndpointUrl = "http://127.0.0.1:5051/debug-base"
                            }
                        ],
                        Configuration: new SlimFaasConfiguration(),
                        Replicas: 1,
                        EndpointReady: true)
                ]
            }
        };
        var proxy = new Proxy(replicasService, "debug-function");

        var queuedRequest = new CustomRequest(
            [new CustomHeader("X-Debug", ["async"])],
            Encoding.UTF8.GetBytes("async-body"),
            "debug-function",
            "/queued/path",
            "POST",
            "?value=1");
        using HttpResponseMessage asyncResponse = await sendClient.SendHttpRequestAsync(
            queuedRequest,
            new SlimFaasDefaultConfiguration(),
            proxy: proxy,
            reservedPodIp: "debug-function-debug");

        DefaultHttpContext context = BuildHttpContext();
        context.Request.Method = "PUT";
        context.Request.Headers["X-Debug"] = "sync";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("sync-body"));
        context.Request.ContentLength = context.Request.Body.Length;
        using HttpResponseMessage syncResponse = await sendClient.SendHttpRequestSync(
            context,
            "debug-function",
            "/sync/path",
            "?value=2",
            new SlimFaasSyncConfiguration(),
            proxy: proxy);

        Assert.Equal(
            new Uri("http://127.0.0.1:5051/debug-base/queued/path?value=1"),
            captured[0].Uri);
        Assert.Equal(("POST", "async-body", "async"),
            (captured[0].Method, captured[0].Body, captured[0].DebugHeader));
        Assert.Equal(
            new Uri("http://127.0.0.1:5051/debug-base/sync/path?value=2"),
            captured[1].Uri);
        Assert.Equal(("PUT", "sync-body", "sync"),
            (captured[1].Method, captured[1].Body, captured[1].DebugHeader));
    }

    [Fact]
    public async Task Sync_streams_large_non_seekable_body_without_disposing_the_source()
    {
        byte[] payload = Enumerable.Range(0, 512 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();
        byte[]? received = null;
        using var httpClient = new HttpClient(new HttpMessageHandlerStub(
            async (request, cancellationToken) =>
            {
                received = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("OK")
                };
            }));
        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(provider => provider.CurrentNamespace).Returns("default");
        var sendClient = new SendClient(
            httpClient,
            new Mock<ILogger<SendClient>>().Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                BaseFunctionUrl = "http://{function_name}:8080/",
                Namespace = "default"
            }),
            namespaceProvider.Object,
            new SlimFaas.Endpoints.NetworkActivityTracker());
        using var source = new NonSeekableReadStream(new MemoryStream(payload));
        DefaultHttpContext context = BuildHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Body = source;
        context.Request.ContentLength = payload.Length;
        context.Request.ContentType = "application/octet-stream";

        using HttpResponseMessage response = await sendClient.SendHttpRequestSync(
            context,
            "fibonacci",
            "/upload",
            "",
            new SlimFaasSyncConfiguration());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload, received);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Sync_forwards_client_cancellation()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new HttpMessageHandlerStub(
            async (_, cancellationToken) =>
            {
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(provider => provider.CurrentNamespace).Returns("default");
        var sendClient = new SendClient(
            httpClient,
            new Mock<ILogger<SendClient>>().Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                BaseFunctionUrl = "http://{function_name}:8080/",
                Namespace = "default"
            }),
            namespaceProvider.Object,
            new SlimFaas.Endpoints.NetworkActivityTracker());
        using var clientCancellation = new CancellationTokenSource();
        DefaultHttpContext context = BuildHttpContext();
        context.RequestAborted = clientCancellation.Token;

        Task<HttpResponseMessage> responseTask = sendClient.SendHttpRequestSync(
            context,
            "fibonacci",
            "/wait",
            "",
            new SlimFaasSyncConfiguration());
        await requestStarted.Task;
        await clientCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
    }

    [Fact]
    public async Task Sync_releases_reserved_target_when_forwarding_fails()
    {
        using var httpClient = new HttpClient(new HttpMessageHandlerStub(
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("target unavailable"))));
        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.SetupGet(provider => provider.CurrentNamespace).Returns("default");
        var sendClient = new SendClient(
            httpClient,
            new Mock<ILogger<SendClient>>().Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                BaseFunctionUrl = "http://{pod_ip}:{pod_port}/",
                Namespace = "default"
            }),
            namespaceProvider.Object,
            new SlimFaas.Endpoints.NetworkActivityTracker());
        var proxy = new Mock<IProxy>();
        proxy.Setup(item => item.AcquireNextIPForSync()).Returns("pod-1");
        proxy.Setup(item => item.GetPorts("pod-1")).Returns([8080]);
        proxy.Setup(item => item.ResolvePodIp("pod-1")).Returns("127.0.0.1");
        DefaultHttpContext context = BuildHttpContext();

        await Assert.ThrowsAsync<HttpRequestException>(() => sendClient.SendHttpRequestSync(
            context,
            "fibonacci",
            "/fail",
            "",
            new SlimFaasSyncConfiguration(),
            proxy: proxy.Object));

        proxy.Verify(item => item.ReleaseSyncIP("pod-1"), Times.Once);
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

internal sealed class NonSeekableReadStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
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
