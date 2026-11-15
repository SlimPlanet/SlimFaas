using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MemoryPack;

namespace SlimData.ClusterFiles;

public sealed class DiskFileRepository : IDurableFileRepository
{
    private const string MetaExt = ".meta.mp";
    // Versions are written to temporary files. ReplaceFile temporarily retains
    // write access to a replacement, so concurrent readers must share it too.
    private const FileShare ReadSharing = FileShare.ReadWrite | FileShare.Delete;
    private readonly string _root;
    private readonly ILogger<DiskFileRepository> _logger;
    private readonly IFileCacheControl _cacheControl;
    private readonly IFileDurability _durability;
    private readonly Lazy<string> _storageIdentity;
    private readonly string _storageRoot;
    private readonly List<string> _createdDirectoryParents;
    private readonly KeyedAsyncLock _mutations = new(long.MaxValue);

    public DiskFileRepository(
        string pathDirectory,
        ILogger<DiskFileRepository> logger,
        bool dropPageCache = true,
        bool durableWrites = false,
        string? storageRoot = null)
        : this(pathDirectory, logger, new LinuxFileCacheControl(dropPageCache, logger), new FileDurability(durableWrites), storageRoot)
    {
    }

    internal DiskFileRepository(
        string pathDirectory,
        ILogger<DiskFileRepository> logger,
        IFileCacheControl cacheControl,
        IFileDurability? durability = null,
        string? storageRoot = null)
    {
        if (string.IsNullOrWhiteSpace(pathDirectory))
            throw new ArgumentException("pathDirectory is required", nameof(pathDirectory));

        _root = pathDirectory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheControl = cacheControl ?? throw new ArgumentNullException(nameof(cacheControl));
        _durability = durability ?? new FileDurability(enabled: false);
        string identityRoot = Path.GetFullPath(storageRoot ?? _root);
        _storageRoot = identityRoot;
        string relative = Path.GetRelativePath(identityRoot, Path.GetFullPath(_root));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Files storage identity root must contain the file repository.", nameof(storageRoot));
        _storageIdentity = new Lazy<string>(() => FileStorageIdentity.LoadOrCreate(identityRoot));
        var createdParents = new List<string>();
        {
            string? current = Path.GetFullPath(_root);
            while (current is not null && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
                if (current is not null)
                    createdParents.Add(current);
            }
        }
        _createdDirectoryParents = createdParents;
        Directory.CreateDirectory(_root);
        string rootParent = Path.GetDirectoryName(Path.GetFullPath(_root)) ?? _root;
        if (_durability.Enabled && !createdParents.Contains(rootParent, StringComparer.Ordinal))
            createdParents.Add(rootParent);
        foreach (string parent in createdParents)
            _durability.FlushDirectory(parent);
    }

    public string StorageIdentity => _storageIdentity.Value;

    public async Task<FilePersistenceReceipt> PersistVersionAsync(string id, string sha256Hex, long length, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        await using var mutation = await _mutations.AcquireAsync(id, ct).ConfigureAwait(false);
        FileMetadata? metadata = await TryGetVersionMetadataAsync(id, sha256Hex, ct).ConfigureAwait(false);
        if (metadata is null || metadata.Length != length)
            throw new IOException("The requested file revision metadata is unavailable.");
        string bodyPath = VersionBodyPath(id, sha256Hex);
        string metadataPath = VersionMetadataPath(id, sha256Hex);
        bool archived = File.Exists(metadataPath);
        if (!archived)
        {
            bodyPath = GetPaths(id).FilePath;
            metadataPath = GetPaths(id).MetaPath;
        }
        var durability = new FileDurability(enabled: true);
        await using (var body = new FileStream(bodyPath, FileMode.Open, FileAccess.ReadWrite, ReadSharing,
                         bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            try
            {
                if (body.Length != length)
                    throw new IOException("The requested file revision length is invalid.");
                byte[] actual = await FileContentHash.ComputeSha256Async(body, ct).ConfigureAwait(false);
                if (!Convert.ToHexString(actual).Equals(sha256Hex, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The requested file revision checksum is invalid.");
                ct.ThrowIfCancellationRequested();
                durability.FlushFile(body);
            }
            finally
            {
                _cacheControl.Drop(body);
            }
        }
        using (var storedMetadata = new FileStream(metadataPath, FileMode.Open, FileAccess.ReadWrite, ReadSharing))
            durability.FlushFile(storedMetadata);
        if (archived)
            durability.FlushDirectory(Path.Combine(_root, ".versions"));
        string? directory = Path.GetFullPath(_root);
        while (directory is not null)
        {
            durability.FlushDirectory(directory);
            if (string.Equals(directory, _storageRoot, StringComparison.Ordinal))
                break;
            directory = Path.GetDirectoryName(directory);
        }
        foreach (string parent in _createdDirectoryParents)
            durability.FlushDirectory(parent);
        return new FilePersistenceReceipt(id, metadata.Sha256Hex, length, StorageIdentity);
    }

    public Task<FilePutResult> SaveAsync(
        string id,
        Stream content,
        string contentType,
        bool overwrite,
        long? expireAtUtcTicks,
        CancellationToken ct,
        IDictionary<string, string>? tags=null)
        => SaveCoreAsync(id, content, contentType, overwrite, expireAtUtcTicks, tags, expected: null, ct);

    public Task<FilePutResult> SaveVerifiedAsync(
        string id,
        Stream content,
        FilePutResult expected,
        long? expireAtUtcTicks,
        CancellationToken ct,
        IDictionary<string, string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfNegative(expected.Length);
        return SaveCoreAsync(id, content, expected.ContentType, overwrite: true, expireAtUtcTicks, tags, expected, ct);
    }

    private async Task<FilePutResult> SaveCoreAsync(
        string id,
        Stream content,
        string contentType,
        bool overwrite,
        long? expireAtUtcTicks,
        IDictionary<string, string>? tags,
        FilePutResult? expected,
        CancellationToken ct)
    {
        await using var mutation = await _mutations.AcquireAsync(id, ct).ConfigureAwait(false);
        var (filePath, metaPath) = GetPaths(id);
        if (!overwrite && (File.Exists(filePath) || File.Exists(metaPath)))
            throw new IOException($"File already exists: {id}");
        var tmp = filePath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var fs = new FileStream(
                             tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 128 * 1024, options: FileOptions.Asynchronous))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    while (true)
                    {
                        var read = await content.ReadAsync(buffer, ct).ConfigureAwait(false);
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
                _durability.FlushFile(fs);
                _cacheControl.Drop(fs);
            }

            var shaHex = ToLowerHex(hash.GetHashAndReset());
            if (expected is not null &&
                (total != expected.Length || !shaHex.Equals(expected.Sha256Hex, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("Downloaded file does not match its expected length and SHA-256.");
            }

            ct.ThrowIfCancellationRequested();
            var meta = new FileMetadata(contentType, shaHex, total, expireAtUtcTicks, tags);
            // Keep the previous revision while the caller's Raft metadata can
            // still name it. The current metadata file remains a local hint.
            FileMetadata? previous = await TryGetMetadataAsync(id, ct).ConfigureAwait(false);
            bool versioned = File.Exists(VersionMetadataPath(id, shaHex)) ||
                             previous is not null && !string.Equals(previous.Sha256Hex, shaHex, StringComparison.OrdinalIgnoreCase);
            if (!versioned)
            {
                // Fresh immutable IDs and same-hash repairs keep the historical
                // two-file layout. They do not need extra revision metadata or links.
                MoveIntoPlace(tmp, filePath, overwrite);
                await WriteMetadataAsync(metaPath, meta, _logger, _durability, ct).ConfigureAwait(false);
                _durability.FlushDirectory(_root);
                return new FilePutResult(shaHex, contentType, total);
            }
            Directory.CreateDirectory(Path.Combine(_root, ".versions"));
            if (previous is not null && IsHash(previous.Sha256Hex))
            {
                await WriteMetadataAsync(VersionMetadataPath(id, previous.Sha256Hex), previous, _logger, _durability, ct).ConfigureAwait(false);
                string previousBody = VersionBodyPath(id, previous.Sha256Hex);
                if (File.Exists(filePath) && !File.Exists(previousBody))
                    MoveIntoPlace(filePath, previousBody, overwrite: false);
            }

            // Metadata may briefly describe an unavailable pending body; no
            // reader considers it available until the corresponding bytes exist.
            await WriteMetadataAsync(VersionMetadataPath(id, shaHex), meta, _logger, _durability, ct).ConfigureAwait(false);
            MoveIntoPlace(tmp, VersionBodyPath(id, shaHex), overwrite: true);
            await WriteMetadataAsync(metaPath, meta, _logger, _durability, ct).ConfigureAwait(false);
            // Keep the historical current-body path readable by older releases
            // without storing a second copy of the body on the same volume.
            string alias = filePath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                FileVersionLink.Create(VersionBodyPath(id, shaHex), alias);
                MoveIntoPlace(alias, filePath, overwrite: true);
            }
            finally
            {
                TryDelete(alias);
            }
            _durability.FlushDirectory(Path.Combine(_root, ".versions"));
            _durability.FlushDirectory(_root);

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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string> paths = Directory.EnumerateFiles(_root, "*" + MetaExt, SearchOption.TopDirectoryOnly);
        string versions = Path.Combine(_root, ".versions");
        if (Directory.Exists(versions))
            paths = paths.Concat(Directory.EnumerateFiles(versions, "*" + MetaExt, SearchOption.TopDirectoryOnly));
        foreach (var metaPath in paths)
        {
            ct.ThrowIfCancellationRequested();

            var file = Path.GetFileName(metaPath);
            if (string.IsNullOrWhiteSpace(file) || !file.EndsWith(MetaExt, StringComparison.OrdinalIgnoreCase))
                continue;

            var safe = file[..^MetaExt.Length];
            int versionSeparator = safe.IndexOf('.', StringComparison.Ordinal);
            if (versionSeparator >= 0)
                safe = safe[..versionSeparator];
            string id;
            try { id = Base64UrlCodec.Decode(safe); }
            catch (Exception ex)
            {
                _logger.LogFailedToDecodeBase64FilenameSkipping(ex, metaPath);
                continue;
            }

            FileMetadata? meta;
            try
            {
                meta = await ReadMetadataAsync(metaPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogFailedToReadMetadataFileSkipping(ex, metaPath);
                continue;
            }

            if (meta is null) continue;
            if (seen.Add(id + ":" + meta.Sha256Hex.ToLowerInvariant()))
                yield return new FileMetadataEntry(id, meta);
        }
    }

    public async Task<bool> ExistsAsync(string id, string sha256Hex, CancellationToken ct)
    {
        var meta = await TryGetVersionMetadataAsync(id, sha256Hex, ct).ConfigureAwait(false);
        if (meta is null || !meta.Sha256Hex.Equals(sha256Hex, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            await using Stream body = await OpenReadVersionAsync(id, sha256Hex, ct).ConfigureAwait(false);
            if (body.Length != meta.Length)
                return false;
            byte[] actualHash = await FileContentHash.ComputeSha256Async(body, ct).ConfigureAwait(false);
            return Convert.ToHexString(actualHash).Equals(sha256Hex, StringComparison.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public async Task<FileMetadata?> TryGetMetadataAsync(string id, CancellationToken ct)
    {
        var (_, metaPath) = GetPaths(id);
        if (!File.Exists(metaPath))
            return null;

        try { return await ReadMetadataAsync(metaPath, ct).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task<FileMetadata?> TryGetVersionMetadataAsync(string id, string sha256Hex, CancellationToken ct)
    {
        if (!IsHash(sha256Hex))
            return null;
        string path = VersionMetadataPath(id, sha256Hex);
        FileMetadata? metadata;
        try
        {
            metadata = File.Exists(path)
                ? await ReadMetadataAsync(path, ct).ConfigureAwait(false)
                : await TryGetMetadataAsync(id, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return metadata is not null && string.Equals(metadata.Sha256Hex, sha256Hex, StringComparison.OrdinalIgnoreCase)
            ? metadata : null;
    }

    public async Task<Stream> OpenReadAsync(string id, CancellationToken ct)
    {
        FileMetadata? metadata = await TryGetMetadataAsync(id, ct).ConfigureAwait(false);
        if (metadata is null)
            return OpenBody(GetPaths(id).FilePath);
        return await OpenReadVersionAsync(id, metadata.Sha256Hex, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadVersionAsync(string id, string sha256Hex, CancellationToken ct)
    {
        FileMetadata? metadata = await TryGetVersionMetadataAsync(id, sha256Hex, ct).ConfigureAwait(false);
        if (metadata is null)
            throw new FileNotFoundException("The requested file revision is unavailable.");
        string path = VersionBodyPath(id, sha256Hex);
        if (File.Exists(path))
            return OpenBody(path);
        if (File.Exists(VersionMetadataPath(id, sha256Hex)))
            throw new FileNotFoundException("The requested file revision body is unavailable.");
        // Read old layouts without changing their bytes. A concurrent migration
        // can rename the legacy body between lookup and open; retry its version.
        try { return OpenBody(GetPaths(id).FilePath); }
        catch (FileNotFoundException) { return OpenBody(path); }
    }

    private FileCacheDroppingReadStream OpenBody(string filePath)
    {
#pragma warning disable CA2000 // ownership is transferred to the returned FileCacheDroppingReadStream
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, ReadSharing,
            bufferSize: 128 * 1024, options: FileOptions.Asynchronous);
#pragma warning restore CA2000

        return new FileCacheDroppingReadStream(fs, _cacheControl);
    }

    private (string FilePath, string MetaPath) GetPaths(string id)
    {
        var safe = Base64UrlCodec.Encode(id);
        var file = Path.Combine(_root, safe + ".bin");
        var meta = Path.Combine(_root, safe + MetaExt);
        return (file, meta);
    }

    private string VersionBodyPath(string id, string sha256Hex) =>
        Path.Combine(_root, Base64UrlCodec.Encode(id) + "." + sha256Hex.ToLowerInvariant() + ".bin");

    private string VersionMetadataPath(string id, string sha256Hex)
    {
        string directory = Path.Combine(_root, ".versions");
        return Path.Combine(directory, Base64UrlCodec.Encode(id) + "." + sha256Hex.ToLowerInvariant() + MetaExt);
    }

    private static bool IsHash(string? hash)
    {
        if (hash is null || hash.Length != 64)
            return false;
        foreach (char value in hash)
            if (!char.IsAsciiHexDigit(value))
                return false;
        return true;
    }

    public async Task DeleteVersionAsync(string id, string sha256Hex, CancellationToken ct)
    {
        if (!IsHash(sha256Hex))
            return;
        await using var mutation = await _mutations.AcquireAsync(id, ct).ConfigureAwait(false);
        FileMetadata? current = await TryGetMetadataAsync(id, ct).ConfigureAwait(false);
        string body = VersionBodyPath(id, sha256Hex);
        string metadata = VersionMetadataPath(id, sha256Hex);
        if (File.Exists(body))
            File.Delete(body);
        if (File.Exists(metadata))
            File.Delete(metadata);
        if (current is not null && current.Sha256Hex.Equals(sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            var (filePath, metaPath) = GetPaths(id);
            File.Delete(filePath);
            File.Delete(metaPath);
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var mutation = await _mutations.AcquireAsync(id, ct).ConfigureAwait(false);

        var (filePath, metaPath) = GetPaths(id);

        if (File.Exists(filePath))
            File.Delete(filePath);

        if (File.Exists(metaPath))
            File.Delete(metaPath);

        string prefix = Base64UrlCodec.Encode(id) + ".";
        foreach (string path in Directory.EnumerateFiles(_root, prefix + "*.bin"))
            File.Delete(path);
        string versions = Path.Combine(_root, ".versions");
        if (Directory.Exists(versions))
            foreach (string path in Directory.EnumerateFiles(versions, prefix + "*" + MetaExt))
                File.Delete(path);
    }

    private static void MoveIntoPlace(string tmp, string dst, bool overwrite)
    {
        if (!overwrite && File.Exists(dst))
            throw new IOException($"File already exists: {dst}");

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        // On Windows MoveFileEx cannot overwrite an open destination, even when
        // its readers share deletion. ReplaceFile preserves those readers' versions.
        if (OperatingSystem.IsWindows() && overwrite && File.Exists(dst))
            File.Replace(tmp, dst, destinationBackupFileName: null);
        else
            File.Move(tmp, dst, overwrite);
    }

    private void TryDelete(string path) => TryDelete(path, _logger);

    private static void TryDelete(string path, ILogger<DiskFileRepository> logger)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.LogFailedToDeleteTemporaryFilePath(ex, path); }
    }

    private static async Task<FileMetadata?> ReadMetadataAsync(string metaPath, CancellationToken ct)
    {
        // Metadata is very small => full read is fine, and cancellation is supported.
        ct.ThrowIfCancellationRequested();
        // Atomic replacement must remain possible while a reader finishes its
        // old file version. File.ReadAllBytesAsync does not share deletion on Windows.
        await using var stream = new FileStream(metaPath, FileMode.Open, FileAccess.Read,
            ReadSharing, bufferSize: 1,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > Array.MaxLength)
            throw new IOException("Metadata file exceeds the supported length.");
        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return MemoryPackPayload.Deserialize<FileMetadata>(bytes);
    }

    private static async Task WriteMetadataAsync(string metaPath, FileMetadata meta, ILogger<DiskFileRepository> logger, IFileDurability durability, CancellationToken ct)
    {
        var tmp = metaPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = MemoryPackSerializer.Serialize(meta);

            await using (var stream = new FileStream(
                             tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 16 * 1024, options: FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                durability.FlushFile(stream);
            }

            MoveIntoPlace(tmp, metaPath, overwrite: true);
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

            // Include pending revision metadata without descending into unrelated paths.
            IEnumerable<string> temporaryFiles = Directory.EnumerateFiles(_root, "*.tmp.*", SearchOption.TopDirectoryOnly);
            string versions = Path.Combine(_root, ".versions");
            if (Directory.Exists(versions))
                temporaryFiles = temporaryFiles.Concat(Directory.EnumerateFiles(versions, "*.tmp.*", SearchOption.TopDirectoryOnly));
            foreach (var tmp in temporaryFiles)
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
                    _logger.LogFailedToDeleteOrphanTmpFile(ex, tmp);
                }
            }

            return deleted;
        }, ct);
    }
}
