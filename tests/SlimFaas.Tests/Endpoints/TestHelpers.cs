using System.Net;
using Microsoft.AspNetCore.Http;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

internal class MemoryReplicasService : IReplicasService
{
    public DeploymentsInformations Deployments =>
        new(
            new List<DeploymentInformation>
            {
                new(Replicas: 0, Deployment: "fibonacci", Namespace: "default",
                    Pods: new List<PodInformation> { new("", true, true, "", "", new List<int>() { 5000 }) }, Configuration: new SlimFaasConfiguration())
            }, new SlimFaasDeploymentInformation(1, new List<PodInformation> { new("", true, true, "", "", new List<int>() { 5000 }) }), new List<PodInformation>());

    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();

    public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();

    public async Task SyncDeploymentsFromSlimData(DeploymentsInformations deploymentsInformations)
    {
        await Task.Delay(100);
    }
}

internal class MemoryReplicas2ReplicasService : IReplicasService
{
    public DeploymentsInformations Deployments =>
        new(
            new List<DeploymentInformation>
            {
                new(Replicas: 2,
                    Deployment: "fibonacci",
                    SubscribeEvents: new List<SubscribeEvent>() {
                        new SubscribeEvent("reload", FunctionVisibility.Public),
                        new SubscribeEvent("reloadprivate", FunctionVisibility.Private),
                        new SubscribeEvent("reloadnoprefix", FunctionVisibility.Public),
                    },
                    PathsStartWithVisibility: new List<PathVisibility>()
                    {
                        new PathVisibility("/compute", FunctionVisibility.Public),
                        new PathVisibility("/private", FunctionVisibility.Private),
                        new PathVisibility("/noprefix", FunctionVisibility.Public)
                    },
                    Namespace: "default",
                    Configuration: new SlimFaasConfiguration(),
                    Pods: new List<PodInformation> {
                        new("fibonacci-1", true, true, "0", "fibonacci", new List<int>() { 8080 }),
                        new("fibonacci-2", true, true, "0", "fibonacci", new List<int>() { 8080 }),
                        new("fibonacci-3", false, false, "0", "fibonacci")
                    },
                EndpointReady: true )
            }, new SlimFaasDeploymentInformation(1, new List<PodInformation> { new("", true, true, "", "", new List<int>() { 5000 }) }), new List<PodInformation>());

    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();

    public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();

    public async Task SyncDeploymentsFromSlimData(DeploymentsInformations deploymentsInformations)
    {
        await Task.Delay(100);
    }
}

internal class MemorySlimFaasQueue : ISlimFaasQueue
{
    public Task<IList<QueueData>?> DequeueAsync(string key, int count = 1) => Task.FromResult<IList<QueueData>?>(null);

    public Task<long> CountElementAsync(string key, IList<CountType> countTypes, int maximum) => Task.FromResult(0L);

    public Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus) => Task.CompletedTask;

    public async Task<string> EnqueueAsync(string key, byte[] message, RetryInformation retryInformation)
    {
        await Task.Delay(100);
        return Guid.NewGuid().ToString();
    }
}

internal record SendData(string FunctionName, string Path, string BaseUrl);

internal class SendClientMock : ISendClient
{
    public IList<SendData> SendDatas = new List<SendData>();

    public Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, Proxy? proxy = null)
    {
        HttpResponseMessage responseMessage = new HttpResponseMessage();
        responseMessage.StatusCode = HttpStatusCode.OK;
        Task.Delay(100).Wait();
        SendDatas.Add(new(customRequest.FunctionName, customRequest.Path, baseUrl ?? ""));
        return Task.FromResult(responseMessage);
    }

    public Task<HttpResponseMessage> SendHttpRequestSync(HttpContext httpContext, string functionName,
        string functionPath, string functionQuery, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, Proxy? proxy = null)
    {
        HttpResponseMessage responseMessage = new HttpResponseMessage();
        responseMessage.StatusCode = HttpStatusCode.OK;
        Task.Delay(100).Wait();
        SendDatas.Add(new(functionName, functionPath, baseUrl ?? ""));
        return Task.FromResult(responseMessage);
    }
}

internal class SlimFaasPortsMock : ISlimFaasPorts
{
    public IList<int> Ports
    {
        get { return new List<int> { 5000, 9002 }; }
    }
}

internal class WebSocketFunctionRepositoryMock : IWebSocketFunctionRepository
{
    public IReadOnlyList<DeploymentInformation> GetVirtualDeployments() =>
        Array.Empty<DeploymentInformation>();
}

internal class WebSocketSendClientMock : IWebSocketSendClient
{
    public Task<int> SendAsync(string functionName, CustomRequest customRequest, string elementId,
        bool isLastTry, int tryNumber, CancellationToken ct = default) =>
        Task.FromResult(200);

    public Task PublishEventAsync(string functionName, CustomRequest customRequest,
        string eventName, CancellationToken ct = default) =>
        Task.CompletedTask;
}

