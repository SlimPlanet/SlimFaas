using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNext.IO;

namespace SlimData.ClusterFiles;

public sealed record FileMetadata(string ContentType, string Sha256Hex, long Length);

public interface IFileRepository
{
    Task<FilePutResult> SaveAsync(
        string id,
        Stream content,
        string contentType,
        bool overwrite,
        CancellationToken ct);

    Task<FilePutResult> SaveFromTransferObjectAsync(
        string id,
        IDataTransferObject dto,
        string contentType,
        bool overwrite,
        string? expectedSha256Hex,
        long? expectedLength,
        CancellationToken ct);

    Task<bool> ExistsAsync(string id, string sha256Hex, CancellationToken ct);

    Task<FileMetadata?> TryGetMetadataAsync(string id, CancellationToken ct);

    Task<Stream> OpenReadAsync(string id, CancellationToken ct);
}