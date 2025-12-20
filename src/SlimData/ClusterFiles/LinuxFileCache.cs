using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class LinuxFileCache
{
    private const int POSIX_FADV_DONTNEED = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_fadvise(int fd, long offset, long len, int advice);

    public static void DropCache(FileStream fs)
    {
        if (!OperatingSystem.IsLinux()) return;

        bool addedRef = false;
        try
        {
            SafeFileHandle h = fs.SafeFileHandle;
            h.DangerousAddRef(ref addedRef);
            int fd = (int)h.DangerousGetHandle();
            _ = posix_fadvise(fd, 0, 0, POSIX_FADV_DONTNEED); // 0..0 => tout le fichier
        }
        finally
        {
            if (addedRef) fs.SafeFileHandle.DangerousRelease();
        }
    }
}

