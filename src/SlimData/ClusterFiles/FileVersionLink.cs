using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SlimData.ClusterFiles;

internal static partial class FileVersionLink
{
    internal static void Create(string existingPath, string linkPath)
    {
        bool succeeded = OperatingSystem.IsWindows()
            ? CreateHardLink(linkPath, existingPath, IntPtr.Zero)
            : Link(existingPath, linkPath) == 0;
        if (!succeeded)
            throw new IOException("Could not publish a file revision link.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Link(string existingPath, string linkPath);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string linkPath, string existingPath, IntPtr securityAttributes);
}
