using System.Runtime.InteropServices;

internal static partial class FileCacheHints
{
    private const int POSIX_FADV_DONTNEED = 4;

    public static void DropFromCache(FileStream fs)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var fd = fs.SafeFileHandle.DangerousGetHandle().ToInt32();
        posix_fadvise(fd, 0, 0, POSIX_FADV_DONTNEED);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int posix_fadvise(int fd, long offset, long len, int advice);
}