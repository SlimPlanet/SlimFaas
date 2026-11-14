using Microsoft.Extensions.Caching.Memory;
using Moq;
using SlimFaas;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public class FunctionStatusCacheTests
{
    [Fact]
    public void Detailed_status_preserves_real_then_virtual_order_and_cache_identity()
    {
        var first = new DeploymentInformation("z-first", "default", [], new SlimFaasConfiguration(), 0);
        var second = first with { Deployment = "a-second" };
        var virtualFunction = first with { Deployment = "virtual", PodType = PodType.WebSocket };
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new FunctionStatusCache(memory, CreateWebSocketRepo(virtualFunction));
        var replicas = CreateReplicasService(first, second);

        var result = cache.GetAllDetailed(replicas);
        Assert.Equal(["z-first", "a-second", "virtual"], result.Select(function => function.Name));
        Assert.Same(result, cache.GetAllDetailed(replicas));
    }

    [Fact]
    public void Name_cache_remains_ordinal_and_rejects_exact_duplicates()
    {
        var real = new DeploymentInformation("Function", "default", [], new SlimFaasConfiguration(), 0);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new FunctionStatusCache(memory, CreateWebSocketRepo(real with { Deployment = "function" }));
        var replicas = CreateReplicasService(real);
        Assert.Equal("Function", cache.GetOne(replicas, "Function")?.Name);
        Assert.Equal("function", cache.GetOne(replicas, "function")?.Name);
        Assert.Null(cache.GetOne(replicas, "FUNCTION"));

        using var duplicateMemory = new MemoryCache(new MemoryCacheOptions());
        var duplicates = new FunctionStatusCache(duplicateMemory, CreateWebSocketRepo(real));
        Assert.Throws<ArgumentException>(() => duplicates.GetOne(replicas, "Function"));
    }

    private static IReplicasService CreateReplicasService(params DeploymentInformation[] functions)
    {
        var mock = new Mock<IReplicasService>();
        mock.Setup(r => r.Deployments).Returns(
            new DeploymentsInformations(
                functions.ToList(),
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
                new List<PodInformation>()));
        return mock.Object;
    }

    private static IWebSocketFunctionRepository CreateWebSocketRepo(params DeploymentInformation[] virtualFunctions)
    {
        var mock = new Mock<IWebSocketFunctionRepository>();
        mock.Setup(r => r.GetVirtualDeployments()).Returns(virtualFunctions.ToList());
        return mock.Object;
    }

    [Fact(DisplayName = "GetAll includes both Kubernetes and WebSocket functions")]
    public void GetAll_IncludesBothK8sAndWebSocketFunctions()
    {
        var k8sFunc = new DeploymentInformation(
            Deployment: "fibonacci", Namespace: "default",
            Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration(), Replicas: 0);

        var wsFunc = new DeploymentInformation(
            Deployment: "ws-handler", Namespace: "websocket-virtual",
            Pods: new List<PodInformation> { new("ws-conn1", true, true, "conn1", "ws-handler") },
            Configuration: new SlimFaasConfiguration(), Replicas: 1,
            PodType: PodType.WebSocket);

        var replicasService = CreateReplicasService(k8sFunc);
        var wsRepo = CreateWebSocketRepo(wsFunc);
        var cache = new FunctionStatusCache(new MemoryCache(new MemoryCacheOptions()), wsRepo);

        var result = cache.GetAll(replicasService);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, f => f.Name == "fibonacci" && f.PodType == "Deployment");
        Assert.Contains(result, f => f.Name == "ws-handler" && f.PodType == "WebSocket");
    }

    [Fact(DisplayName = "GetAllDetailed includes both Kubernetes and WebSocket functions")]
    public void GetAllDetailed_IncludesBothK8sAndWebSocketFunctions()
    {
        var k8sFunc = new DeploymentInformation(
            Deployment: "fibonacci", Namespace: "default",
            Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration(), Replicas: 0);

        var wsFunc = new DeploymentInformation(
            Deployment: "ws-handler", Namespace: "websocket-virtual",
            Pods: new List<PodInformation> { new("ws-conn1", true, true, "conn1", "ws-handler") },
            Configuration: new SlimFaasConfiguration(), Replicas: 1,
            PodType: PodType.WebSocket,
            Visibility: FunctionVisibility.Public);

        var replicasService = CreateReplicasService(k8sFunc);
        var wsRepo = CreateWebSocketRepo(wsFunc);
        var cache = new FunctionStatusCache(new MemoryCache(new MemoryCacheOptions()), wsRepo);

        var result = cache.GetAllDetailed(replicasService);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, f => f.Name == "fibonacci" && f.PodType == "Deployment");
        Assert.Contains(result, f => f.Name == "ws-handler" && f.PodType == "WebSocket");
    }

    [Fact(DisplayName = "GetOne returns WebSocket function by name")]
    public void GetOne_ReturnsWebSocketFunctionByName()
    {
        var k8sFunc = new DeploymentInformation(
            Deployment: "fibonacci", Namespace: "default",
            Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration(), Replicas: 0);

        var wsFunc = new DeploymentInformation(
            Deployment: "ws-handler", Namespace: "websocket-virtual",
            Pods: new List<PodInformation> { new("ws-conn1", true, true, "conn1", "ws-handler") },
            Configuration: new SlimFaasConfiguration(), Replicas: 1,
            PodType: PodType.WebSocket);

        var replicasService = CreateReplicasService(k8sFunc);
        var wsRepo = CreateWebSocketRepo(wsFunc);
        var cache = new FunctionStatusCache(new MemoryCache(new MemoryCacheOptions()), wsRepo);

        var result = cache.GetOne(replicasService, "ws-handler");

        Assert.NotNull(result);
        Assert.Equal("ws-handler", result.Name);
        Assert.Equal("WebSocket", result.PodType);
    }

    [Fact(DisplayName = "GetAll with no WebSocket functions returns only K8s functions")]
    public void GetAll_NoWebSocketFunctions_ReturnsOnlyK8s()
    {
        var k8sFunc = new DeploymentInformation(
            Deployment: "fibonacci", Namespace: "default",
            Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration(), Replicas: 0);

        var replicasService = CreateReplicasService(k8sFunc);
        var wsRepo = CreateWebSocketRepo(); // empty
        var cache = new FunctionStatusCache(new MemoryCache(new MemoryCacheOptions()), wsRepo);

        var result = cache.GetAll(replicasService);

        Assert.Single(result);
        Assert.Equal("fibonacci", result[0].Name);
    }
}
