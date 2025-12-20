using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

public static class MemoryDump
{
    /// <summary>
    /// Dumps .NET (GC) + process + cgroup/container + OS (meminfo) memory to Console.WriteLine.
    /// Works best on Linux/Kubernetes; falls back gracefully elsewhere.
    /// </summary>
    public static void Dump(string tag)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var p = Process.GetCurrentProcess();

            // ---- .NET managed ----
            long gcHeap = GC.GetTotalMemory(forceFullCollection: false);
            var gci = GC.GetGCMemoryInfo();
            long totalAvail = gci.TotalAvailableMemoryBytes;   // can be based on cgroup if supported
            long highLoad = gci.HighMemoryLoadThresholdBytes;
            long memLoad = gci.MemoryLoadBytes;

            // ---- process ----
            long ws = p.WorkingSet64;                // RSS-ish
            long priv = p.PrivateMemorySize64;       // private bytes
            long vmem = p.VirtualMemorySize64;       // virtual
            long alloc = GC.GetTotalAllocatedBytes(precise: false);

            // ---- container/cgroup (Linux) ----
            var cg = TryReadCgroup();

            // ---- OS (Linux /proc/meminfo) ----
            var os = TryReadMemInfo();

            Console.WriteLine(
                $"[MEM {now:O}] {tag}\n" +
                $"  .NET: GCHeap={ToMB(gcHeap):n1}MB  AllocatedTotal={ToMB(alloc):n1}MB  " +
                $"MemoryLoad={ToMB(memLoad):n1}MB  HighThreshold={ToMB(highLoad):n1}MB  TotalAvailable={ToMB(totalAvail):n1}MB\n" +
                $"  Proc: WorkingSet={ToMB(ws):n1}MB  Private={ToMB(priv):n1}MB  Virtual={ToMB(vmem):n1}MB\n" +
                $"  Cgrp: Current={ToMBOrQ(cg.CurrentBytes)}MB  Limit={ToMBOrQ(cg.LimitBytes)}MB  " +
                $"Anon={ToMBOrQ(cg.AnonBytes)}MB  FileCache={ToMBOrQ(cg.FileBytes)}MB  InactiveFile={ToMBOrQ(cg.InactiveFileBytes)}MB\n" +
                $"  OS  : MemTotal={ToMBOrQ(os.MemTotalBytes)}MB  MemAvailable={ToMBOrQ(os.MemAvailableBytes)}MB  " +
                $"Cached={ToMBOrQ(os.CachedBytes)}MB  Buffers={ToMBOrQ(os.BuffersBytes)}MB\n"
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEM] Dump failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- helpers ----------------

    private static double ToMB(long bytes) => bytes / 1024d / 1024d;
    private static string ToMBOrQ(long? bytes) => bytes is null ? "?" : (bytes.Value / 1024d / 1024d).ToString("n1", CultureInfo.InvariantCulture);

    private sealed class CgroupInfo
    {
        public long? CurrentBytes { get; init; }
        public long? LimitBytes { get; init; }
        public long? AnonBytes { get; init; }
        public long? FileBytes { get; init; }
        public long? InactiveFileBytes { get; init; }
    }

    private static CgroupInfo TryReadCgroup()
    {
        if (!OperatingSystem.IsLinux())
            return new CgroupInfo();

        // Try cgroup v2 first (common on modern K8s)
        var v2 = TryReadCgroupV2();
        if (v2.CurrentBytes is not null || v2.LimitBytes is not null)
            return v2;

        // Fallback cgroup v1
        return TryReadCgroupV1();
    }

    private static CgroupInfo TryReadCgroupV2()
    {
        try
        {
            const string basePath = "/sys/fs/cgroup";
            var current = ReadLong(Path.Combine(basePath, "memory.current"));
            var limit = ReadLongOrMax(Path.Combine(basePath, "memory.max")); // "max" means unlimited
            var stat = ReadKeyValueLongs(Path.Combine(basePath, "memory.stat"));

            stat.TryGetValue("anon", out var anon);
            stat.TryGetValue("file", out var file);
            stat.TryGetValue("inactive_file", out var inactiveFile);

            return new CgroupInfo
            {
                CurrentBytes = current,
                LimitBytes = limit,
                AnonBytes = anon,
                FileBytes = file,
                InactiveFileBytes = inactiveFile
            };
        }
        catch
        {
            return new CgroupInfo();
        }
    }

    private static CgroupInfo TryReadCgroupV1()
    {
        try
        {
            // Common locations (depends on distro/runtime)
            var basePaths = new[]
            {
                "/sys/fs/cgroup/memory",
                "/sys/fs/cgroup"
            };

            foreach (var basePath in basePaths)
            {
                var cur = ReadLong(Path.Combine(basePath, "memory.usage_in_bytes"));
                var lim = ReadLong(Path.Combine(basePath, "memory.limit_in_bytes"));
                var stat = ReadKeyValueLongs(Path.Combine(basePath, "memory.stat"));

                if (cur is null && lim is null && stat.Count == 0)
                    continue;

                stat.TryGetValue("rss", out var rss);       // not exactly anon, but useful
                stat.TryGetValue("cache", out var cache);   // file cache-ish

                return new CgroupInfo
                {
                    CurrentBytes = cur,
                    LimitBytes = lim,
                    AnonBytes = rss,
                    FileBytes = cache,
                    InactiveFileBytes = null
                };
            }

            return new CgroupInfo();
        }
        catch
        {
            return new CgroupInfo();
        }
    }

    private sealed class MemInfo
    {
        public long? MemTotalBytes { get; init; }
        public long? MemAvailableBytes { get; init; }
        public long? CachedBytes { get; init; }
        public long? BuffersBytes { get; init; }
    }

    private static MemInfo TryReadMemInfo()
    {
        if (!OperatingSystem.IsLinux())
            return new MemInfo();

        try
        {
            var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                // Example: "MemTotal:       16349032 kB"
                var parts = line.Split(new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                if (long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                    dict[parts[0]] = kb * 1024;
            }

            dict.TryGetValue("MemTotal", out var total);
            dict.TryGetValue("MemAvailable", out var avail);
            dict.TryGetValue("Cached", out var cached);
            dict.TryGetValue("Buffers", out var buffers);

            return new MemInfo
            {
                MemTotalBytes = total == 0 ? null : total,
                MemAvailableBytes = avail == 0 ? null : avail,
                CachedBytes = cached == 0 ? null : cached,
                BuffersBytes = buffers == 0 ? null : buffers
            };
        }
        catch
        {
            return new MemInfo();
        }
    }

    private static long? ReadLong(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var s = File.ReadAllText(path).Trim();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static long? ReadLongOrMax(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var s = File.ReadAllText(path).Trim();
            if (string.Equals(s, "max", StringComparison.OrdinalIgnoreCase))
                return null; // unlimited
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, long> ReadKeyValueLongs(string path)
    {
        var dict = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return dict;

            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                if (long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    dict[parts[0]] = v;
            }
        }
        catch
        {
            // ignore
        }
        return dict;
    }
}
