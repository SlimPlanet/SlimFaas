using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

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

/// <summary>
/// Enumerates the live descendants of a process on Windows, where
/// <see cref="Process"/> does not expose parent identifiers.
/// </summary>
internal static partial class WindowsProcessTree
{
    private const uint SnapshotProcesses = 0x00000002;
    private const nint InvalidHandle = -1;

    internal static List<ProcessTreeEntry> GetDescendants(int rootProcessId, DateTime rootStartedAt)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process tree snapshots are only implemented on Windows.");
        return ProcessTree.SelectDescendants(rootProcessId, rootStartedAt, Snapshot(), GetStartTime);
    }

    private static DateTime? GetStartTime(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The process exited in the meantime or cannot be opened.
            return null;
        }
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
