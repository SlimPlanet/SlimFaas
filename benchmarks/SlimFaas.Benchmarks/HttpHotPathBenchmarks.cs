using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Benchmarks.Support;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;

namespace SlimFaas.Benchmarks;

/// <summary>
/// Theme 3 — paths executed on every proxied HTTP request.
/// - Global middleware port check (A/B comparison of the two existing overloads).
/// - Path-prefix visibility resolution (before/after removing the ToLowerInvariant
///   calls inside the loop).
/// - Proxy pod selection for a synchronous call (before/after the single-pass,
///   LINQ-free rewrite). The Proxy is isolated from the cost of
///   ReplicasService.Deployments through a static IReplicasService.
/// </summary>
[MemoryDiagnoser]
public class HttpHotPathBenchmarks
{
    private readonly IList<int> _slimFaasPorts = new List<int> { 5000, 3262 };
    private DeploymentInformation _functionWithRules = null!;
    private Proxy _proxy = null!;

    [GlobalSetup]
    public void Setup()
    {
        var deployments = BenchData.BuildDeployments(functionCount: 20, podsPerFunction: 5);
        _proxy = new Proxy(new StaticReplicasService(deployments), "function-10");

        _functionWithRules = deployments.Functions[0] with
        {
            PathsStartWithVisibility = new List<PathVisibility>
            {
                new("/internal/admin", FunctionVisibility.Private),
                new("/internal/metrics", FunctionVisibility.Private),
                new("/api/private", FunctionVisibility.Private),
                new("/api/public", FunctionVisibility.Public)
            }
        };
    }

    [Benchmark(Baseline = true)]
    public bool PortCheck_WithArrays() =>
        HostPort.IsSamePort([5000, 8080], _slimFaasPorts.ToArray());

    [Benchmark]
    public bool PortCheck_AllocationFree() =>
        HostPort.IsSamePort(5000, 8080, _slimFaasPorts);

    [Benchmark]
    public FunctionVisibility ResolveVisibility_PathRules() =>
        FunctionEndpointsHelpers.GetFunctionVisibility(
            NullLogger.Instance,
            _functionWithRules,
            "/api/private/orders/123/details");

    [Benchmark]
    public string ProxyAcquireReleaseSync()
    {
        string target = _proxy.AcquireNextIPForSync();
        _proxy.ReleaseSyncIP(target);
        return target;
    }
}
