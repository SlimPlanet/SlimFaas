using SlimData;

namespace SlimData.Tests;

public sealed class SlimDataQueueSignalTests
{
    [Fact]
    public async Task Pulse_wakes_every_observer_without_consuming_the_notification()
    {
        var signal = new SlimDataQueueSignal();
        long version = signal.Version;
        Task<long> first = signal.WaitForChangeAsync(version, TimeSpan.FromSeconds(5), CancellationToken.None);
        Task<long> second = signal.WaitForChangeAsync(version, TimeSpan.FromSeconds(5), CancellationToken.None);

        signal.Pulse();

        long[] observed = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(observed, value => Assert.Equal(version + 1, value));
    }

    [Fact]
    public async Task Observer_with_stale_version_returns_immediately()
    {
        var signal = new SlimDataQueueSignal();
        long staleVersion = signal.Version;
        signal.Pulse();

        long observed = await signal.WaitForChangeAsync(
            staleVersion,
            TimeSpan.FromHours(1),
            CancellationToken.None);

        Assert.Equal(staleVersion + 1, observed);
    }

    [Fact]
    public async Task Timeout_keeps_current_version()
    {
        var signal = new SlimDataQueueSignal();
        long version = signal.Version;

        long observed = await signal.WaitForChangeAsync(
            version,
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(version, observed);
    }
}
