using SlimFaas.Kubernetes.Watch;

namespace SlimFaas.Tests.Kubernetes;

public class KubernetesResourceSignalTests
{
    [Fact]
    public async Task ReturnsImmediatelyWhenVersionAlreadyAdvanced()
    {
        var signal = new KubernetesResourceSignal();
        long observed = signal.Version;
        signal.Pulse();

        long result = await signal.WaitForChangeAsync(observed, TimeSpan.FromMinutes(5), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(observed + 1, result);
    }

    [Fact]
    public async Task TimeoutReturnsUnchangedVersion()
    {
        var signal = new KubernetesResourceSignal();
        long observed = signal.Version;

        long result = await signal.WaitForChangeAsync(observed, TimeSpan.FromMilliseconds(50), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(observed, result);
    }

    [Fact]
    public async Task PulseWakesAPendingWaiter()
    {
        var signal = new KubernetesResourceSignal();
        long observed = signal.Version;

        Task<long> waiter = signal.WaitForChangeAsync(observed, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        signal.Pulse();

        long result = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(observed + 1, result);
    }

    [Fact]
    public async Task PulseWhileConsumerBusyIsObservedOnNextWait()
    {
        var signal = new KubernetesResourceSignal();
        long observed = signal.Version;

        // Pulse pendant que le consommateur « travaille » (pas de waiter en attente).
        signal.Pulse();
        signal.Pulse();

        long result = await signal.WaitForChangeAsync(observed, TimeSpan.FromMinutes(5), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(observed + 2, result);
    }
}
