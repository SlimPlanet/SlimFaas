using System.Collections.Immutable;

namespace ClusterFileDemoProdish.Storage;

public sealed record SlimDataState(
    ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>> HashSets,
    ImmutableDictionary<string, ReadOnlyMemory<byte>> KeyValues,
    ImmutableDictionary<string, ImmutableArray<object>> Queues
);
