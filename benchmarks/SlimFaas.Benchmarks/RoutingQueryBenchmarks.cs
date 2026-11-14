using BenchmarkDotNet.Attributes;
using SlimFaas.Benchmarks.Support;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;

namespace SlimFaas.Benchmarks;

[MemoryDiagnoser]
public class RoutingQueryBenchmarks
{
    private StaticReplicasService _replicas = null!;
    private Proxy _proxy = null!;
    private DeploymentInformation _function = null!;
    private string _functionName = null!;

    [Params(2, 20, 200)]
    public int FunctionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var deployments = BenchData.BuildDeployments(FunctionCount, podsPerFunction: 5);
        _replicas = new StaticReplicasService(deployments);
        _function = deployments.Functions[^1];
        _functionName = _function.Deployment;
        _proxy = new Proxy(_replicas, _functionName);
    }

    [Benchmark]
    public DeploymentInformation? SearchFunction() =>
        FunctionEndpointsHelpers.SearchFunction(_replicas, _functionName);

    [Benchmark]
    public IList<int>? FirstReadyPodPorts() => _proxy.GetPorts();

    [Benchmark]
    public int StatusReadyCount() => FunctionEndpointsHelpers.MapToFunctionStatus(_function).NumberReady;
}
