using MemoryPack;

namespace SlimData;

[MemoryPackable]
public partial record QueueData(string Id, byte[] Data, int TryNumber, bool IsLastTry, long LastRetryTimeTicks, long HttpTimeoutTicks);

[MemoryPackable]
public partial record ListItems 
{
    public List<QueueData>? Items { get; set; }
}