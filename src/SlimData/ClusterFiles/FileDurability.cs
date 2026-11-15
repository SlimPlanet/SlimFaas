using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SlimData.ClusterFiles;

internal interface IFileDurability
{
    bool Enabled { get; }
    void FlushFile(FileStream stream);
    void FlushDirectory(string path);
}

internal sealed partial class FileDurability(bool enabled) : IFileDurability
{
    public bool Enabled => enabled;

    public void FlushFile(FileStream stream)
    {
        if (enabled)
            stream.Flush(flushToDisk: true);
    }

    public void FlushDirectory(string path)
    {
        if (!enabled)
            return;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Durable Files directory publication requires Linux or macOS.");
        int descriptor;
        do { descriptor = Open(path, flags: 0); }
        while (descriptor < 0 && Marshal.GetLastPInvokeError() == 4); // POSIX EINTR
        if (descriptor < 0)
            throw new IOException("Could not open the Files directory for persistence.", new Win32Exception(Marshal.GetLastPInvokeError()));
        using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        RandomAccess.FlushToDisk(handle);
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Open(string path, int flags);
}
