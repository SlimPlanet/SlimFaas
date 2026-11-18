namespace SlimData.ClusterFiles;

public interface IDurableClusterFileSync : IClusterFileSync
{
    /// <summary>
    /// Confirms persisted local and remote bytes at this point in time. Callers
    /// must separately retain durable admission/references through publication.
    /// </summary>
    Task<FileCopyReceipt> ConfirmRemoteCopyAsync(string id, string sha256Hex, long length, CancellationToken ct);
}
