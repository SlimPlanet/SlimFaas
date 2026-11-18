using MemoryPack;

namespace SlimData;

internal static class SlimDataCommandBatchResponseCodec
{
    internal static byte[] Serialize(SlimDataCommandBatchResponse response)
    {
        long payloadBytes = 0;
        foreach (SlimDataBatchOperationResult result in response.Results ?? [])
        {
            foreach (QueueData item in result.QueueItems ?? [])
            {
                payloadBytes += item?.Data?.Length ?? 0;
                if (payloadBytes >= 65536)
                    return MemoryPackExactSerializer.Serialize(response);
            }
        }
        return MemoryPackSerializer.Serialize(response);
    }
}
