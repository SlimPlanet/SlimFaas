namespace ClusterFileDemoProdish.Storage;

public interface IKvStore
{
    Task<byte[]?> GetAsync(string key);
    Task SetAsync(string key, byte[] value, long? timeToLiveMilliseconds = null);
    Task DeleteAsync(string key);
}
