using System.Text;

namespace SlimData.ClusterFiles;

internal static class FileStorageIdentity
{
    internal static string LoadOrCreate(string storageRoot)
    {
        const string markerName = ".slimfaas-files-storage-identity";
        var durability = new FileDurability(enabled: true);
        string marker = Path.Combine(storageRoot, markerName);
        if (!File.Exists(marker))
        {
            string temporary = marker + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N") + "\n"));
                    durability.FlushFile(stream);
                }
                try { File.Move(temporary, marker, overwrite: false); }
                catch (IOException) when (File.Exists(marker)) { }
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        durability.FlushDirectory(storageRoot);
        if (new FileInfo(marker).Length > 64)
            throw new IOException("Invalid Files storage identity marker.");
        string value = File.ReadAllText(marker).Trim();
        if (!Guid.TryParseExact(value, "N", out Guid identifier) || identifier == Guid.Empty)
            throw new IOException("Invalid Files storage identity marker.");
        return identifier.ToString("N");
    }
}
