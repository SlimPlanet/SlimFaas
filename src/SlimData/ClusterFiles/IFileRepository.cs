using DotNext.IO;
using MemoryPack;

namespace SlimData.ClusterFiles;
[MemoryPackable]
public partial record FileMetadata(string ContentType, string Sha256Hex, long Length, long? ExpireAtUtcTicks);
public sealed record FileMetadataEntry(string Id, FileMetadata Metadata);

public interface IFileRepository
{
    Task<FilePutResult> SaveAsync(
        string id,
        Stream content,
        string contentType,
        bool overwrite,
        long? expireAtUtcTicks,
        CancellationToken ct);
    

    Task<bool> ExistsAsync(string id, string sha256Hex, CancellationToken ct);

    Task<FileMetadata?> TryGetMetadataAsync(string id, CancellationToken ct);
    
    IAsyncEnumerable<FileMetadataEntry> EnumerateAllMetadataAsync(CancellationToken ct);
    
    Task DeleteAsync(string id, CancellationToken ct);

    Task<Stream> OpenReadAsync(string id, CancellationToken ct);

    /// <summary>
    /// Deletes orphan .tmp files (interrupted uploads) that have not been modified for more than 10 minutes.
    /// </summary>
    Task<int> CleanupOrphanTempFilesAsync(CancellationToken ct);
}