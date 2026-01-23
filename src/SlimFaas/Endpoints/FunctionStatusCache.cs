using Microsoft.Extensions.Caching.Memory;

namespace SlimFaas.Endpoints;

public sealed class FunctionStatusCache
{
    private readonly IMemoryCache _cache;

    public FunctionStatusCache(IMemoryCache cache) => _cache = cache;

    public IReadOnlyList<FunctionStatus> GetAll(IReplicasService replicasService)
    {
        // Cache très court : protège CPU en cas de polling agressif
        return _cache.GetOrCreate("status-all", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(300);

            // IMPORTANT: lis une seule fois
            var deployments = replicasService.Deployments;

            // Evite ToList si tu peux (array direct)
            return deployments.Functions
                .Select(FunctionEndpointsHelpers.MapToFunctionStatus)
                .ToList();
        })!;
    }

    public FunctionStatus? GetOne(IReplicasService replicasService, string functionName)
    {
        // Cache court + dictionnaire pour éviter O(n) sur chaque appel
        var dict = _cache.GetOrCreate("status-byname", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(1000);
            var deployments = replicasService.Deployments;

            return deployments.Functions.ToDictionary(
                f => f.Deployment,
                FunctionEndpointsHelpers.MapToFunctionStatus,
                StringComparer.Ordinal
            );
        })!;

        return dict.TryGetValue(functionName, out var status) ? status : null;
    }
}
