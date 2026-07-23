using System.Runtime.InteropServices;

namespace SlimData.ClusterFiles;

internal interface IFileCacheControl
{
    void Drop(FileStream stream);
}

internal sealed class LinuxFileCacheControl(
    bool enabled,
    ILogger<DiskFileRepository> logger) : IFileCacheControl
{
    private const int PosixFadvDontNeed = 4;

    public void Drop(FileStream stream)
    {
        if (!enabled || !OperatingSystem.IsLinux())
            return;

        var addedReference = false;
        try
        {
            stream.SafeFileHandle.DangerousAddRef(ref addedReference);
            var fileDescriptor = checked((int)stream.SafeFileHandle.DangerousGetHandle());
            var result = LinuxFileCacheNative.PosixFadvise(fileDescriptor, 0, 0, PosixFadvDontNeed);
            if (result != 0)
            {
                logger.LogDebug(
                    "Unable to advise Linux to release file cache. Result={Result}",
                    result);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to advise Linux to release file cache.");
        }
        finally
        {
            if (addedReference)
                stream.SafeFileHandle.DangerousRelease();
        }
    }
}

internal static partial class LinuxFileCacheNative
{
    [LibraryImport("libc", EntryPoint = "posix_fadvise")]
    internal static partial int PosixFadvise(
        int fileDescriptor,
        long offset,
        long length,
        int advice);
}

internal sealed class FileCacheDroppingReadStream(
    FileStream inner,
    IFileCacheControl cacheControl) : Stream
{
    private FileStream? _inner = inner;

    private FileStream Inner =>
        Volatile.Read(ref _inner) ?? throw new ObjectDisposedException(nameof(FileCacheDroppingReadStream));

    public override bool CanRead => Volatile.Read(ref _inner)?.CanRead ?? false;
    public override bool CanSeek => Volatile.Read(ref _inner)?.CanSeek ?? false;
    public override bool CanWrite => false;
    public override long Length => Inner.Length;

    public override long Position
    {
        get => Inner.Position;
        set => Inner.Position = value;
    }

    public override void Flush() => Inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        Inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => Inner.Read(buffer);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        Inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        Inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var stream = Interlocked.Exchange(ref _inner, null);
            if (stream is not null)
            {
                cacheControl.Drop(stream);
                stream.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _inner, null);
        if (stream is not null)
        {
            cacheControl.Drop(stream);
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}
