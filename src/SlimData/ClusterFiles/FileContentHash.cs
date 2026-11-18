using System.Buffers;
using System.Security.Cryptography;

namespace SlimData.ClusterFiles;

internal static class FileContentHash
{
    private const int BufferSize = 128 * 1024;

    internal static async ValueTask<byte[]> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int count;
            // Match the repository's file-stream buffer so reads go directly
            // into reusable storage instead of allocating a second large buffer.
            while ((count = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) != 0)
                hash.AppendData(buffer, 0, count);
            ct.ThrowIfCancellationRequested();
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
