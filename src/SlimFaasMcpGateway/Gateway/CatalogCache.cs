using Microsoft.Extensions.Caching.Memory;

namespace SlimFaasMcpGateway.Gateway;

public sealed record CachedResponse(int StatusCode, string ContentType, byte[] Body);

public interface ICatalogCache
{
    bool TryGet(string key, out CachedResponse response);
    void Set(string key, CachedResponse response, TimeSpan ttl);
}

public sealed class CatalogCache : ICatalogCache
{
    private readonly IMemoryCache _cache;

    public CatalogCache(IMemoryCache cache) => _cache = cache;

    public bool TryGet(string key, out CachedResponse response)
        => _cache.TryGetValue(key, out response!);

    public void Set(string key, CachedResponse response, TimeSpan ttl)
    {
        _cache.Set(key, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        });
    }
}
