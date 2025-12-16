using ClusterFileDemoProdish.Models;
using ClusterFileDemoProdish.Options;
using DotNext.Net.Cluster.Messaging;
using Microsoft.Extensions.Options;
using System.Net.Mime;
using System.Security.Cryptography;
using DotNext.IO;

namespace ClusterFileDemoProdish.Storage;

public interface IFileRepository
{
    string GetPath(string id);

    Task<(FileMeta Meta, string Path)> SaveAsync(string id, Stream body, string contentType, string? fileName, long? ttlMs, CancellationToken ct);
    Task<(bool Exists, string Path)> TryGetAsync(string id, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);

    Task<(bool Ok, string? Sha256Hex, long SizeBytes)> WriteFromStreamAsync(string id, Stream source, CancellationToken ct);
    Task<(bool Ok, string? Sha256Hex, long SizeBytes)> WriteFromDataTransferObjectAsync(string id, IDataTransferObject dto, int chunkSize, CancellationToken ct);
}

public sealed class FileRepository : IFileRepository
{
    private readonly FileStorageOptions _opt;
    private readonly ILogger<FileRepository> _logger;

    public FileRepository(IOptions<FileStorageOptions> opt, ILogger<FileRepository> logger)
    {
        _opt = opt.Value;
        _logger = logger;
        Directory.CreateDirectory(_opt.RootPath);
    }

    public string GetPath(string id)
        => Path.Combine(_opt.RootPath, id);

    public async Task<(FileMeta Meta, string Path)> SaveAsync(string id, Stream body, string contentType, string? fileName, long? ttlMs, CancellationToken ct)
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long? expiresUtc = ttlMs is long t && t > 0 ? created + t : null;

        var tmp = GetPath(id) + ".tmp";
        var final = GetPath(id);

        Directory.CreateDirectory(_opt.RootPath);

        await using var file = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        using var sha = SHA256.Create();
        long size = 0;

        var buffer = new byte[Math.Max(4 * 1024, _opt.BroadcastChunkSizeBytes)];
        int read;
        while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            size += read;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        await file.FlushAsync(ct);

        if (File.Exists(final))
            File.Delete(final);

        File.Move(tmp, final);

        var meta = new FileMeta
        {
            Id = id,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? MediaTypeNames.Application.Octet : contentType,
            FileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName,
            SizeBytes = size,
            Sha256Hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant(),
            CreatedUtcMs = created,
            ExpiresUtcMs = expiresUtc
        };

        return (meta, final);
    }

    public Task<(bool Exists, string Path)> TryGetAsync(string id, CancellationToken ct)
    {
        var path = GetPath(id);
        return Task.FromResult((File.Exists(path), path));
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        var path = GetPath(id);
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete file {Path}", path); }
        }
        return Task.CompletedTask;
    }

    public async Task<(bool Ok, string? Sha256Hex, long SizeBytes)> WriteFromStreamAsync(string id, Stream source, CancellationToken ct)
    {
        var tmp = GetPath(id) + ".pull.tmp";
        var final = GetPath(id);

        Directory.CreateDirectory(_opt.RootPath);

        await using var file = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        await source.CopyToAsync(file, _opt.PullChunkSizeBytes, ct);
        await file.FlushAsync(ct);

        if (File.Exists(final))
            File.Delete(final);

        File.Move(tmp, final);

        var (sha, len) = await ComputeShaAndLenAsync(final, ct);
        return (true, sha, len);
    }

    public async Task<(bool Ok, string? Sha256Hex, long SizeBytes)> WriteFromDataTransferObjectAsync(string id, IDataTransferObject dto, int chunkSize, CancellationToken ct)
    {
        var tmp = GetPath(id) + ".dto.tmp";
        var final = GetPath(id);

        Directory.CreateDirectory(_opt.RootPath);

        await using (var file = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        }))
        {
            await DotNext.IO.DataTransferObject.WriteToAsync(dto, file, chunkSize, ct);
            await file.FlushAsync(ct);
        }

        if (File.Exists(final))
            File.Delete(final);

        File.Move(tmp, final);

        var (sha, len) = await ComputeShaAndLenAsync(final, ct);
        return (true, sha, len);
    }

    private static async Task<(string? Sha256Hex, long Length)> ComputeShaAndLenAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        using var sha = SHA256.Create();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), stream.Length);
    }
}
