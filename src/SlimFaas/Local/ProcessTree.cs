using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SlimFaas.Local;

/// <summary>A live process and the identifier of the process that created it.</summary>
internal readonly record struct ProcessTreeEntry(int ProcessId, int ParentProcessId);

internal static class ProcessTree
{
    /// <summary>
    /// Selects the descendants of <paramref name="rootProcessId"/> among <paramref name="processes"/>.
    /// Windows keeps the parent identifier of a process after its parent has exited and reuses
    /// identifiers, so a candidate is only accepted when it started after the process it names as
    /// its parent. A candidate whose start time cannot be read is skipped, which is also what
    /// <see cref="Process.Kill(bool)"/> does with processes it cannot open.
    /// </summary>
    internal static List<ProcessTreeEntry> SelectDescendants(
        int rootProcessId,
        DateTime rootStartedAt,
        IReadOnlyCollection<ProcessTreeEntry> processes,
        Func<int, DateTime?> startTimeOf)
    {
        ILookup<int, ProcessTreeEntry> children = processes.ToLookup(entry => entry.ParentProcessId);
        var descendants = new List<ProcessTreeEntry>();
        var visited = new HashSet<int> { rootProcessId };
        var pending = new Queue<(int ProcessId, DateTime StartedAt)>();
        pending.Enqueue((rootProcessId, rootStartedAt));

        while (pending.TryDequeue(out (int ProcessId, DateTime StartedAt) parent))
        {
            foreach (ProcessTreeEntry child in children[parent.ProcessId])
            {
                if (!visited.Add(child.ProcessId))
                    continue;
                if (startTimeOf(child.ProcessId) is not { } childStartedAt || childStartedAt < parent.StartedAt)
                    continue;
                descendants.Add(child);
                pending.Enqueue((child.ProcessId, childStartedAt));
            }
        }

        return descendants;
    }
}

/// <summary>Keeps process handles alive so ancestry and reused identifiers cannot hide a survivor.</summary>
internal sealed class ProcessTreeSnapshot(
    IReadOnlyList<(ProcessTreeEntry Entry, SafeProcessHandle Handle)> descendants) : IDisposable
{
    internal List<ProcessTreeEntry> GetLiveDescendants()
        => descendants.Where(descendant => !WindowsProcessTree.HasExited(descendant.Handle))
            .Select(descendant => descendant.Entry).ToList();

    public void Dispose()
    {
        foreach ((_, SafeProcessHandle handle) in descendants)
            handle.Dispose();
    }
}

/// <summary>
/// Enumerates the live descendants of a process on Windows, where
/// <see cref="Process"/> does not expose parent identifiers.
/// </summary>
internal static partial class WindowsProcessTree
{
    private const uint SnapshotProcesses = 0x00000002;
    private const nint InvalidHandle = -1;

    internal static ProcessTreeSnapshot CaptureDescendants(int rootProcessId, DateTime rootStartedAt)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process tree snapshots are only implemented on Windows.");

        var opened = new Dictionary<int, SafeProcessHandle>();
        try
        {
            List<ProcessTreeEntry> entries = ProcessTree.SelectDescendants(
                rootProcessId, rootStartedAt.ToUniversalTime(), Snapshot(), processId =>
                {
                    // Query and synchronization rights suffice; no termination or write access is needed.
                    const uint QueryAndSynchronize = 0x00101000;
                    SafeProcessHandle handle = OpenProcess(QueryAndSynchronize, false, processId);
                    try
                    {
                        // Retain the handle, not just the PID: HasExited must keep referring
                        // to this process even after its parent exits or Windows reuses a PID.
                        if (handle.IsInvalid || !GetProcessTimes(handle, out long created, out _, out _, out _))
                            throw new Win32Exception();
                        opened.Add(processId, handle);
                        return DateTime.FromFileTimeUtc(created);
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
                });
            var retained = entries.Select(entry => (entry, opened[entry.ProcessId])).ToArray();
            var snapshot = new ProcessTreeSnapshot(retained);
            foreach (ProcessTreeEntry entry in entries)
                opened.Remove(entry.ProcessId);
            return snapshot;
        }
        finally
        {
            foreach (SafeProcessHandle handle in opened.Values)
                handle.Dispose();
        }
    }

    internal static bool HasExited(SafeProcessHandle handle)
    {
        return WaitForSingleObject(handle, 0) switch
        {
            0 => true,
            0x00000102 => false, // WAIT_TIMEOUT: the process handle is not signaled yet.
            _ => throw new Win32Exception()
        };
    }

    private static unsafe List<ProcessTreeEntry> Snapshot()
    {
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandle)
            throw new Win32Exception();

        try
        {
            var entries = new List<ProcessTreeEntry>();
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            if (!Process32FirstW(snapshot, ref entry))
                throw new Win32Exception();
            do
            {
                entries.Add(new ProcessTreeEntry((int)entry.ProcessId, (int)entry.ParentProcessId));
            } while (Process32NextW(snapshot, ref entry));

            const int NoMoreFiles = 18;
            int error = Marshal.GetLastPInvokeError();
            if (error != NoMoreFiles)
                throw new Win32Exception(error);

            return entries;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    // PROCESSENTRY32W
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeProcessHandle OpenProcess(
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
