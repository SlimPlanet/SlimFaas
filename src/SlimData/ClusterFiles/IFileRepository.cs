using DotNext.IO;
using MemoryPack;

namespace SlimData.ClusterFiles;
[MemoryPackable]
public partial record FileMetadata(string ContentType, string Sha256Hex, long Length, long? ExpireAtUtcTicks, IDictionary<string, string>? Tags=null);
public sealed record FileMetadataEntry(string Id, FileMetadata Metadata);

public interface IFileRepository
{
    Task<FilePutResult> SaveAsync(
        string id,
        Stream content,
        string contentType,
        bool overwrite,
        long? expireAtUtcTicks,
        CancellationToken ct,
        IDictionary<string, string>? tags = null);

    /// <summary>
    /// Replaces a file only after the temporary body matches the expected length and hash.
    /// A failed transfer leaves the previous version intact.
    /// </summary>
    Task<FilePutResult> SaveVerifiedAsync(
        string id,
        Stream content,
        FilePutResult expected,
        long? expireAtUtcTicks,
        CancellationToken ct,
        IDictionary<string, string>? tags = null);

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
