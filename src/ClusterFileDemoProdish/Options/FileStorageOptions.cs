namespace ClusterFileDemoProdish.Options;

public sealed record FileStorageOptions
{
    public string RootPath { get; init; } = "data";
    public int PullConcurrency { get; init; } = 4;
    public int BroadcastChunkSizeBytes { get; init; } = 64 * 1024;
    public int PullChunkSizeBytes { get; init; } = 64 * 1024;
}
