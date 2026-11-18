using System.Globalization;
using System.Text;

namespace SlimData;

internal static class WalChunkSize
{
    internal const int WindowsDefault = 64 * 1024;
    internal const string MarkerFileName = "slimdata-chunk-size";

    internal static int Resolve(string location, bool isWindows)
    {
        string marker = Path.Combine(location, MarkerFileName);
        string? storedSize = ReadMarker(marker);
        if (storedSize is not null)
        {
            if (!int.TryParse(storedSize, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int chunkSize) || chunkSize < 4096 || chunkSize % 4096 != 0)
            {
                throw new InvalidDataException("Invalid SlimData WAL chunk size marker: " + marker);
            }

            return chunkSize;
        }

        // DotNext addresses data by chunk size, which cannot change on replay.
        // Unmarked existing WALs keep their historical system-page geometry.
        if (!isWindows || Directory.Exists(location))
            return Environment.SystemPageSize;

        // Windows otherwise creates and synchronously flushes a separate mapped
        // file for every 4 KiB. Large entries can outlast the Raft RPC budget.
        // Persist the geometry before the first WAL page can be created.
        Directory.CreateDirectory(location);
        using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 64, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(WindowsDefault.ToString(CultureInfo.InvariantCulture)));
        stream.Flush(flushToDisk: true);
        return WindowsDefault;
    }

    private static string? ReadMarker(string marker)
    {
        try
        {
            return File.ReadAllText(marker);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
