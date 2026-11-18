using System.Collections.Immutable;
using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SlimData;
using SlimFaas.Benchmarks.Support;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.WebSocket;

namespace SlimFaas.Benchmarks;

[MemoryDiagnoser]
public class CollectionPipelineBenchmarks : IDisposable
{
    private StaticReplicasService _replicas = null!;
    private JobService _jobs = null!;
    private DefaultHttpContext _context = null!;
    private MemoryCache _cache = null!;
    private FunctionStatusCache _statuses = null!;
    private ServiceProvider _services = null!;
    private Dictionary<string, string[]> _headers = null!;
    private SlimDataState _queueState = null!;
    private string[] _connectionIds = null!;
    private readonly WebSocketFunctionConfiguration _configuration = new();

    [Params(20, 200)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var deployments = BenchData.BuildDeployments(Count, podsPerFunction: 5);
        _replicas = new StaticReplicasService(deployments);
        var jobs = BenchData.BuildJobs(Count);
        _jobs = new JobService(new StubKubernetesService(deployments, jobs), new StubJobConfiguration(),
            new StubJobQueue(), new StubNamespaceProvider(), NullLogger<JobService>.Instance);
        _jobs.SyncJobsAsync().GetAwaiter().GetResult();
        _services = new ServiceCollection()
            .AddSingleton<ILogger<FunctionEndpointsHelpersLog>>(NullLogger<FunctionEndpointsHelpersLog>.Instance)
            .BuildServiceProvider();
        _context = new DefaultHttpContext { RequestServices = _services };
        _context.Request.Method = "GET";
        _context.Connection.RemoteIpAddress = IPAddress.Parse(jobs[^1].Ips[0]);
        _headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        _connectionIds = new string[Count];
        for (int i = 0; i < Count; i++)
        {
            string name = $"X-Header-{i}";
            _context.Request.Headers[name] = "value";
            _headers[name] = ["value"];
            _connectionIds[i] = i.ToString("D32", System.Globalization.CultureInfo.InvariantCulture);
        }
        _headers["Connection"] = ["Upgrade, X-Header-0"];
        _headers["Upgrade"] = ["websocket"];
        _cache = new MemoryCache(new MemoryCacheOptions());
        _statuses = new FunctionStatusCache(_cache, new EmptyVirtualFunctions());
        _queueState = new SlimDataState(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty.Add(
                "Queue:benchmark", BenchData.BuildRunningQueue(Count, DateTime.UtcNow.Ticks)));
    }

    [GlobalCleanup]
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cache?.Dispose();
            _services?.Dispose();
        }
    }

    [Benchmark]
    public string JobCallerLookup() =>
        FunctionEndpointsHelpers.ResolveNetworkActivityCaller(_context, _jobs).Actor;

    [Benchmark]
    public Task<CustomRequest> ProjectRequestHeaders() =>
        FunctionEndpointsHelpers.InitCustomRequest(_context, _context.Request, "function", "echo");

    [Benchmark]
    public IReadOnlyList<FunctionStatusDetailed> ProjectDetailedStatuses()
    {
        _cache.Remove("status-all-detailed");
        return _statuses.GetAllDetailed(_replicas);
    }

    [Benchmark]
    public Dictionary<string, string[]> FilterPeerHeaders() => WebSocketPeerHeaders.Filter(_headers);

    [Benchmark]
    public DeploymentInformation ProjectVirtualDeployment() =>
        WebSocketFunctionRepository.CreateDeployment("virtual", _configuration, _connectionIds);

    [Benchmark]
    public SlimDataState StageManagedQueue() => DeadLetterStateMachine.StageQueue(_queueState, "Queue:benchmark");

    private sealed class EmptyVirtualFunctions : IWebSocketFunctionRepository
    {
        public IReadOnlyList<DeploymentInformation> GetVirtualDeployments() => [];
    }
}
