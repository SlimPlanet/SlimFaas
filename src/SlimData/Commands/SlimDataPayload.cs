using System.Collections.Immutable;

namespace SlimData.Commands;


public struct SlimDataPayload
{
    public ImmutableDictionary<string, ReadOnlyMemory<byte>> KeyValues { get; set; }
    
    public ImmutableDictionary<string, ImmutableList<QueueElement>> Queues { get; set; }
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> Hashsets { get; set; }
    
}

