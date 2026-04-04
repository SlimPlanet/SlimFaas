using Microsoft.Extensions.Caching.Memory;
using SlimFaas.Kubernetes;
using SlimFaas.WebSocket;

namespace SlimFaas.Endpoints;

public sealed class FunctionStatusCache
{
    private readonly IMemoryCache _cache;
    private readonly IWebSocketFunctionRepository _webSocketFunctionRepository;

    public FunctionStatusCache(IMemoryCache cache, IWebSocketFunctionRepository webSocketFunctionRepository)
    {
        _cache = cache;
        _webSocketFunctionRepository = webSocketFunctionRepository;
    }

    private IEnumerable<DeploymentInformation> AllFunctions(IReplicasService replicasService)
    {
        return replicasService.Deployments.Functions
            .Concat(_webSocketFunctionRepository.GetVirtualDeployments());
    }

    public IReadOnlyList<FunctionStatus> GetAll(IReplicasService replicasService)
    {
        // Cache très court : protège CPU en cas de polling agressif
        return _cache.GetOrCreate("status-all", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(300);

            return AllFunctions(replicasService)
                .Select(FunctionEndpointsHelpers.MapToFunctionStatus)
                .ToList();
        })!;
    }

    public IReadOnlyList<FunctionStatusDetailed> GetAllDetailed(IReplicasService replicasService)
    {
        return _cache.GetOrCreate("status-all-detailed", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(300);
            return AllFunctions(replicasService)
                .Select(FunctionEndpointsHelpers.MapToFunctionStatusDetailed)
                .ToList();
        })!;
    }

    public FunctionStatus? GetOne(IReplicasService replicasService, string functionName)
    {
        // Cache court + dictionnaire pour éviter O(n) sur chaque appel
        var dict = _cache.GetOrCreate("status-byname", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(1000);

            return AllFunctions(replicasService).ToDictionary(
                f => f.Deployment,
                FunctionEndpointsHelpers.MapToFunctionStatus,
                StringComparer.Ordinal
            );
        })!;

        return dict.TryGetValue(functionName, out var status) ? status : null;
    }
}
