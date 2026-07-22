using System.Net;
using System.Reflection;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public sealed class KubernetesServiceHttpDisposalTests
{
    [Theory]
    [InlineData(PodType.Deployment, HttpStatusCode.OK, "/deployments/function/scale")]
    [InlineData(PodType.StatefulSet, HttpStatusCode.OK, "/statefulsets/function/scale")]
    [InlineData(PodType.Deployment, HttpStatusCode.InternalServerError, "/deployments/function/scale")]
    [InlineData(PodType.StatefulSet, HttpStatusCode.InternalServerError, "/statefulsets/function/scale")]
    public async Task ScaleAsync_disposes_http_resources(
        PodType podType,
        HttpStatusCode statusCode,
        string expectedPathSuffix)
    {
        var handler = new TrackingHandler(statusCode);
        var service = BuildService(handler, out var client);
        using (client)
        {
            var request = new ReplicaRequest("function", "test", 2, podType);

            var result = await service.ScaleAsync(request);

            Assert.Same(request, result);
            Assert.Equal(HttpMethod.Patch, handler.RequestMethod);
            Assert.EndsWith(expectedPathSuffix, handler.RequestPath, StringComparison.Ordinal);
            Assert.Equal("{\"spec\": {\"replicas\": 2}}", handler.RequestBody);
            await AssertRequestContentDisposedAsync(handler);
            Assert.True(handler.ResponseContent?.IsDisposed);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.Accepted, false)]
    [InlineData(HttpStatusCode.NoContent, false)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    public async Task DeleteJobAsync_disposes_http_resources(
        HttpStatusCode statusCode,
        bool throws)
    {
        var handler = new TrackingHandler(statusCode);
        var service = BuildService(handler, out var client);
        using (client)
        {
            if (throws)
            {
                await Assert.ThrowsAsync<HttpOperationException>(
                    () => service.DeleteJobAsync("test", "job"));
            }
            else
            {
                await service.DeleteJobAsync("test", "job");
            }

            Assert.Equal(HttpMethod.Delete, handler.RequestMethod);
            Assert.EndsWith(
                "/apis/batch/v1/namespaces/test/jobs/job?propagationPolicy=Foreground",
                handler.RequestPath,
                StringComparison.Ordinal);
            Assert.Null(handler.RequestBody);
            Assert.Null(handler.RequestContent);
            Assert.True(handler.ResponseContent?.IsDisposed);
        }
    }

    private static async Task AssertRequestContentDisposedAsync(TrackingHandler handler)
    {
        Assert.NotNull(handler.RequestContent);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => handler.RequestContent.ReadAsByteArrayAsync());
    }

    private static KubernetesService BuildService(
        DelegatingHandler handler,
        out k8s.Kubernetes client)
    {
        var config = new KubernetesClientConfiguration { Host = "http://localhost" };
        client = new k8s.Kubernetes(config, handler);

        var service = (KubernetesService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(KubernetesService));

        typeof(KubernetesService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);
        typeof(KubernetesService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, NullLogger<KubernetesService>.Instance);

        return service;
    }

    private sealed class TrackingHandler(HttpStatusCode statusCode) : DelegatingHandler
    {
        public HttpMethod? RequestMethod { get; private set; }
        public string? RequestPath { get; private set; }
        public string? RequestBody { get; private set; }
        public HttpContent? RequestContent { get; private set; }
        public TrackingContent? ResponseContent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestPath = request.RequestUri?.PathAndQuery;
            RequestContent = request.Content;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            ResponseContent = new TrackingContent();
            return new HttpResponseMessage(statusCode)
            {
                Content = ResponseContent,
                RequestMessage = request
            };
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool IsDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0L;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
