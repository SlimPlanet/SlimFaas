using MemoryPack;

namespace SlimData;

[MemoryPackable]
public partial record QueueData(string Id, byte[] Data, int TryNumber, bool IsLastTry);

[MemoryPackable]
public partial record ListItems 
{
    public List<QueueData>? Items { get; set; }
}