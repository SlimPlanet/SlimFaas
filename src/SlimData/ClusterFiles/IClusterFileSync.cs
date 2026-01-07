using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SlimData.ClusterFiles;

public interface IClusterFileSync
{
    Task<FilePutResult> BroadcastFilePutAsync(
        string id,
        Stream content,
        string contentType,
        long contentLengthBytes,
        bool overwrite,
        long? ttl,
        CancellationToken ct);

    Task<FilePullResult> PullFileIfMissingAsync(
        string id,
        string sha256Hex,
        string? preferredNode,
        CancellationToken ct);
}