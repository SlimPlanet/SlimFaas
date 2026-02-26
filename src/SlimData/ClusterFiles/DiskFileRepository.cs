using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MemoryPack;
using Microsoft.Extensions.Logging;

namespace SlimData.ClusterFiles;

public sealed class DiskFileRepository : IFileRepository
{
    private const string MetaExt = ".meta.mp";
    private readonly string _root;
    private readonly ILogger<DiskFileRepository> _logger;

    public DiskFileRepository(string pathDirectory, ILogger<DiskFileRepository> logger)
    {
        if (string.IsNullOrWhiteSpace(pathDirectory))
            throw new ArgumentException("pathDirectory is required", nameof(pathDirectory));

        _root = pathDirectory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Directory.CreateDirectory(_root);
    }

    public async Task<FilePutResult> SaveAsync(
        string id,
        Stream content,
        string contentType,
        bool overwrite,
        long? expireAtUtcTicks,
        CancellationToken ct)
    {
        var (filePath, metaPath) = GetPaths(id);
        var tmp = filePath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            await using var fs = new FileStream(
                tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024, options: FileOptions.Asynchronous);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long total = 0;

            try
            {
                while (true)
                {
                    var read = await content.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    hash.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            
            await fs.FlushAsync(ct).ConfigureAwait(false);
            MoveIntoPlace(tmp, filePath, overwrite);

            var shaHex = ToLowerHex(hash.GetHashAndReset());
            var meta = new FileMetadata(contentType, shaHex, total, expireAtUtcTicks);
            await WriteMetadataAsync(metaPath, meta, ct, _logger).ConfigureAwait(false);

            return new FilePutResult(shaHex, contentType, total);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public async IAsyncEnumerable<FileMetadataEntry> EnumerateAllMetadataAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var metaPath in Directory.EnumerateFiles(_root, "*" + MetaExt, SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();

            var file = Path.GetFileName(metaPath);
            if (string.IsNullOrWhiteSpace(file) || !file.EndsWith(MetaExt, StringComparison.OrdinalIgnoreCase))
                continue;

            var safe = file[..^MetaExt.Length];
            string id;
            try { id = Base64UrlCodec.Decode(safe); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decode Base64 filename, skipping. path={Path}", metaPath);
                continue;
            }

            FileMetadata? meta;
            try
            {
                meta = await ReadMetadataAsync(metaPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read metadata file, skipping. path={Path}", metaPath);
                continue;
            }

            if (meta is null) continue;
            yield return new FileMetadataEntry(id, meta);
        }
    }

    public async Task<bool> ExistsAsync(string id, string sha256Hex, CancellationToken ct)
    {
        var meta = await TryGetMetadataAsync(id, ct).ConfigureAwait(false);
        return meta is not null &&
               meta.Sha256Hex.Equals(sha256Hex, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(GetPaths(id).FilePath);
    }

    public async Task<FileMetadata?> TryGetMetadataAsync(string id, CancellationToken ct)
    {
        var (_, metaPath) = GetPaths(id);
        if (!File.Exists(metaPath))
            return null;

        return await ReadMetadataAsync(metaPath, ct).ConfigureAwait(false);
    }

    public Task<Stream> OpenReadAsync(string id, CancellationToken ct)
    {
        var (filePath, _) = GetPaths(id);
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, options: FileOptions.Asynchronous);

        return Task.FromResult<Stream>(fs);
    }

    private (string FilePath, string MetaPath) GetPaths(string id)
    {
        var safe = Base64UrlCodec.Encode(id);
        var file = Path.Combine(_root, safe + ".bin");
        var meta = Path.Combine(_root, safe + MetaExt);
        return (file, meta);
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var (filePath, metaPath) = GetPaths(id);

        if (File.Exists(filePath))
            File.Delete(filePath);

        if (File.Exists(metaPath))
            File.Delete(metaPath);

        return Task.CompletedTask;
    }

    private static void MoveIntoPlace(string tmp, string dst, bool overwrite)
    {
        if (!overwrite && File.Exists(dst))
            throw new IOException($"File already exists: {dst}");

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Move(tmp, dst, overwrite);
    }

    private void TryDelete(string path) => TryDelete(path, _logger);

    private static void TryDelete(string path, ILogger<DiskFileRepository> logger)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to delete temporary file. path={Path}", path); }
    }

    private static async Task<FileMetadata?> ReadMetadataAsync(string metaPath, CancellationToken ct)
    {
        // Metadata is very small => full read is fine, and cancellation is supported.
        ct.ThrowIfCancellationRequested();
        var bytes = await File.ReadAllBytesAsync(metaPath, ct).ConfigureAwait(false);
        return MemoryPackSerializer.Deserialize<FileMetadata>(bytes);
    }

    private static async Task WriteMetadataAsync(string metaPath, FileMetadata meta, CancellationToken ct, ILogger<DiskFileRepository> logger)
    {
        var tmp = metaPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = MemoryPackSerializer.Serialize(meta);

            await using var s = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 16 * 1024, options: FileOptions.Asynchronous);

            await s.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);

            File.Move(tmp, metaPath, overwrite: true);
        }
        catch
        {
            TryDelete(tmp, logger);
            throw;
        }
    }

    private static string ToLowerHex(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>
    /// Age threshold beyond which a .tmp file is considered orphaned (hard-coded, no need to make it configurable).
    /// </summary>
    private static readonly TimeSpan OrphanTmpThreshold = TimeSpan.FromMinutes(10);

    public Task<int> CleanupOrphanTempFilesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Directory.EnumerateFiles, File.GetLastWriteTimeUtc and File.Delete have no
        // natively async API in .NET. We offload the entire block to the ThreadPool
        // via Task.Run to avoid blocking the caller thread (e.g. a BackgroundService
        // on the thread pool) and stay consistent with the async/await style of the rest
        // of the repository.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var cutoff = DateTime.UtcNow - OrphanTmpThreshold;
            var deleted = 0;

        // Matches both generated patterns: "*.bin.tmp.*" and "*.meta.mp.tmp.*"
            foreach (var tmp in Directory.EnumerateFiles(_root, "*.tmp.*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(tmp);
                    if (lastWrite > cutoff)
                        continue;

                    File.Delete(tmp);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete orphan .tmp file. path={Path}", tmp);
                }
            }

            return deleted;
        }, ct);
    }
}
