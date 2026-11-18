using MemoryPack;

namespace SlimData.Tests;

public sealed class SlimDataCommandBatchResponseCodecTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 64)]
    [InlineData(1, 65535)]
    [InlineData(1, 65536)]
    [InlineData(3, 1048576)]
    [InlineData(64, 1048576)]
    public void Large_queue_replies_keep_the_original_wire_bytes_and_all_delivery_fields(int count, int payloadBytes)
    {
        var response = new SlimDataCommandBatchResponse
        {
            ProducerId = "producer-\u65e5\u672c",
            Sequence = 42,
            RequestId = "request-\U0001f680",
            Duplicate = true,
            ExpectedSequence = 43,
            ErrorMessage = "diagnostic-\u00e9",
            Results = [new SlimDataBatchOperationResult
            {
                Kind = SlimDataBatchOperationKind.ListRightPop,
                RequestId = "pop",
                Applied = true,
                QueueItems = Enumerable.Range(0, count).Select(index => new QueueData(
                    $"element-{index}", Enumerable.Repeat((byte)(index % 251), payloadBytes).ToArray(),
                    index, index == count - 1, 123456789, 300000000, "10.0.0.1")).ToArray()
            }]
        };
        byte[] original = MemoryPackSerializer.Serialize(response);

        byte[] encoded = SlimDataCommandBatchResponseCodec.Serialize(response);

        Assert.Equal(original, encoded);
        SlimDataCommandBatchResponse restored = MemoryPackPayload.Deserialize<SlimDataCommandBatchResponse>(encoded)!;
        Assert.True(restored.Duplicate);
        Assert.Equal(response.ExpectedSequence, restored.ExpectedSequence);
        QueueData[] items = Assert.Single(restored.Results).QueueItems!;
        Assert.Equal(count, items.Length);
        for (int index = 0; index < count; index++)
        {
            QueueData expected = response.Results[0].QueueItems![index];
            Assert.Equal(expected.Data, items[index].Data);
            Assert.Equal(expected with { Data = items[index].Data }, items[index]);
        }
    }
}
