using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Tests.Database;

public sealed class SlimDataBatchModeTests
{
    [Fact]
    public void Low_load_controller_keeps_ten_requests_per_second_on_fast_path()
    {
        long timestamp = 0;
        var controller = new SlimDataBatchLoadController(
            10d,
            () => timestamp,
            timestampFrequency: 1_000);

        controller.RecordArrival();
        timestamp = 100;
        controller.RecordArrival();
        timestamp = 200;
        controller.RecordArrival();
        timestamp = 300;
        controller.RecordArrival();

        var snapshot = controller.GetSnapshot();
        Assert.Equal(SlimDataBatchLoadLevel.Low, snapshot.Level);
        Assert.Equal(10d, snapshot.RequestsPerSecond, precision: 3);
        Assert.Equal(TimeSpan.Zero, snapshot.Timing.Delay);
        Assert.Equal(TimeSpan.Zero, snapshot.Timing.CoalesceWindow);
    }

    [Fact]
    public void Low_load_controller_uses_hysteresis_between_all_load_levels()
    {
        long timestamp = 0;
        var controller = new SlimDataBatchLoadController(
            10d,
            () => timestamp,
            timestampFrequency: 1_000);

        for (var index = 0; index < 4; index++)
        {
            timestamp = index * 80;
            controller.RecordArrival();
        }
        controller.RecordArrival();

        var medium = controller.GetSnapshot();
        Assert.Equal(SlimDataBatchLoadLevel.Medium, medium.Level);
        Assert.Equal(TimeSpan.FromMilliseconds(225), medium.Timing.Delay);

        for (var index = 4; index < 19; index++)
            controller.RecordArrival();

        var high = controller.GetSnapshot();
        Assert.Equal(SlimDataBatchLoadLevel.High, high.Level);
        Assert.Equal(TimeSpan.FromMilliseconds(100), high.Timing.Delay);

        timestamp += 5_001;
        var returnedToLow = controller.GetSnapshot();
        Assert.Equal(SlimDataBatchLoadLevel.Low, returnedToLow.Level);
        Assert.Equal(TimeSpan.Zero, returnedToLow.Timing.Delay);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Key_partitioning_is_stable_and_uses_every_partition(int partitionCount)
    {
        var seen = new HashSet<int>();
        for (var index = 0; index < 2_000; index++)
        {
            var key = $"slimdata-key-{index}";
            var first = SlimDataService.GetPartitionIndex(key, partitionCount);
            var second = SlimDataService.GetPartitionIndex(key, partitionCount);

            Assert.Equal(first, second);
            Assert.InRange(first, 0, partitionCount - 1);
            seen.Add(first);
        }

        Assert.Equal(partitionCount, seen.Count);
    }

    [Fact]
    public void Partition_capacity_limiter_bounds_all_partitions_together()
    {
        var limiter = new SlimDataBatchCapacityLimiter(maxItems: 2, maxBytes: 10);
        limiter.Reserve("commands-p00", 4);
        limiter.Reserve("commands-p01", 6);

        Assert.Throws<BatchQueueFullException>(() =>
            limiter.Reserve("commands-p02", 1));

        limiter.Release(4);
        limiter.Reserve("commands-p02", 4);
    }
}
