using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DotNext.IO;
using MemoryPack;

namespace SlimData.ClusterFiles;

public sealed class DiskFileRepository : IFileRepository
{
    private const string MetaExt = ".meta.mp";
    private readonly string _root;

    public DiskFileRepository(string pathDirectory)
    {
        if (string.IsNullOrWhiteSpace(pathDirectory))
            throw new ArgumentException("pathDirectory is required", nameof(pathDirectory));

        _root = pathDirectory;
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
            await WriteMetadataAsync(metaPath, meta, ct).ConfigureAwait(false);

            return new FilePutResult(shaHex, contentType, total);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public async Task<FilePutResult> SaveFromTransferObjectAsync(
        string id,
        IDataTransferObject dto,
        string contentType,
        bool overwrite,
        string? expectedSha256Hex,
        long? expectedLength,
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
            await using var hashing = new HashingWriteStream(fs, hash);

            // IMPORTANT: on force l’écriture du DTO vers un Stream avec bufferSize explicite
            await DotNext.IO.DataTransferObject.WriteToAsync(dto, hashing, 128 * 1024, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);

            var shaHex = ToLowerHex(hash.GetHashAndReset());
            var length = hashing.BytesWritten;

            if (expectedLength is not null && expectedLength.Value != length)
                throw new InvalidDataException($"Length mismatch. Expected={expectedLength} Actual={length}");

            if (!string.IsNullOrWhiteSpace(expectedSha256Hex) &&
                !shaHex.Equals(expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA256 mismatch. Expected={expectedSha256Hex} Actual={shaHex}");

            MoveIntoPlace(tmp, filePath, overwrite);

            var meta = new FileMetadata(contentType, shaHex, length, expireAtUtcTicks);
            await WriteMetadataAsync(metaPath, meta, ct).ConfigureAwait(false);

            return new FilePutResult(shaHex, contentType, length);
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
            catch { continue; }

            FileMetadata? meta;
            try
            {
                meta = await ReadMetadataAsync(metaPath, ct).ConfigureAwait(false);
            }
            catch
            {
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static async Task<FileMetadata?> ReadMetadataAsync(string metaPath, CancellationToken ct)
    {
        // Meta très petit => lecture complète OK, et cancellation supportée.
        ct.ThrowIfCancellationRequested();
        var bytes = await File.ReadAllBytesAsync(metaPath, ct).ConfigureAwait(false);
        return MemoryPackSerializer.Deserialize<FileMetadata>(bytes);
    }

    private static async Task WriteMetadataAsync(string metaPath, FileMetadata meta, CancellationToken ct)
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
            TryDelete(tmp);
            throw;
        }
    }

    private static string ToLowerHex(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash;

        public long BytesWritten { get; private set; }

        public HashingWriteStream(Stream inner, IncrementalHash hash)
        {
            _inner = inner;
            _hash = hash;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            BytesWritten += count;
            _inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            BytesWritten += buffer.Length;
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
