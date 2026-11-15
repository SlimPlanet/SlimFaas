namespace SlimData.ClusterFiles;

public sealed record FilePersistenceReceipt(string Id, string Sha256Hex, long Length, string StorageIdentity);

public interface IDurableFileRepository : IFileRepository
{
    string StorageIdentity { get; }

    Task<FilePersistenceReceipt> PersistVersionAsync(
        string id,
        string sha256Hex,
        long length,
        CancellationToken ct);
}
