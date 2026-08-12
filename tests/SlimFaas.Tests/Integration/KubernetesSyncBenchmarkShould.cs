using System.Diagnostics;
using Xunit.Abstractions;

namespace SlimFaas.Tests.Integration;

// Benchmark of the integration scenario: the same activity window is executed in
// polling mode and in watch mode, measuring the number of Kubernetes API LIST
// requests and the change-propagation latency. Validates the optimization with
// deliberately loose CI-stable thresholds and prints the comparison table for the
// PR description. The polling cadence is accelerated (100 ms instead of the real
// 3 s / 1 s / 1 s) so a few seconds of test represent minutes of real time; the
// LIST-count ratio between the two modes is what matters.
[Collection("KubernetesSyncIntegration")]
public sealed class KubernetesSyncBenchmarkShould(ITestOutputHelper output)
{
    private const int ObservationWindowMilliseconds = 4000;

    private sealed record BenchmarkResult(
        int TotalListRequests,
        IReadOnlyDictionary<string, int> ListRequestsByResource,
        double PropagationLatencyMilliseconds);

    [Fact]
    public async Task UseFarFewerApiCallsInWatchModeForTheSameActivity()
    {
        BenchmarkResult polling = await RunWindowAsync(watchEnabled: false);
        BenchmarkResult watch = await RunWindowAsync(watchEnabled: true);

        output.WriteLine($"Observation window: {ObservationWindowMilliseconds} ms, polling cadence 100 ms (accelerated)");
        output.WriteLine("");
        output.WriteLine($"| Mode    | LIST requests | Propagation latency |");
        output.WriteLine($"|---------|--------------:|--------------------:|");
        output.WriteLine($"| polling | {polling.TotalListRequests,13} | {polling.PropagationLatencyMilliseconds,17:F0} ms |");
        output.WriteLine($"| watch   | {watch.TotalListRequests,13} | {watch.PropagationLatencyMilliseconds,17:F0} ms |");
        output.WriteLine("");
        output.WriteLine("LIST requests by resource (polling): " +
                         string.Join(", ", polling.ListRequestsByResource.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        output.WriteLine("LIST requests by resource (watch):   " +
                         string.Join(", ", watch.ListRequestsByResource.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));

        // Validation de l'optimisation : au moins 4× moins de LIST à activité égale.
        Assert.True(
            watch.TotalListRequests <= polling.TotalListRequests / 4,
            $"watch mode should issue at most 25% of polling LIST requests " +
            $"(watch={watch.TotalListRequests}, polling={polling.TotalListRequests})");

        // La latence de propagation doit rester raisonnable dans les deux modes
        // (seuils larges pour la stabilité en CI ; les valeurs mesurées sont
        // rapportées ci-dessus).
        Assert.True(polling.PropagationLatencyMilliseconds < 3000,
            $"polling propagation latency too high: {polling.PropagationLatencyMilliseconds:F0} ms");
        Assert.True(watch.PropagationLatencyMilliseconds < 3000,
            $"watch propagation latency too high: {watch.PropagationLatencyMilliseconds:F0} ms");
    }

    private static async Task<BenchmarkResult> RunWindowAsync(bool watchEnabled)
    {
        var cluster = new FakeKubernetesCluster(KubernetesSyncStack.Namespace);
        cluster.AddFunctionDeployment("fibonacci", replicas: 2);
        cluster.AddFunctionPod("fibonacci", "fibonacci-pod-0", "10.0.0.1");
        cluster.AddFunctionPod("fibonacci", "fibonacci-pod-1", "10.0.0.2");

        await using var stack = new KubernetesSyncStack(
            cluster,
            watchEnabled,
            pollingCadenceMilliseconds: 100);
        await stack.StartAsync();
        await KubernetesSyncStack.WaitUntilAsync(
            () => stack.ReplicasService.Deployments.Functions.Any(f => f.Deployment == "fibonacci"),
            "initial sync");

        // Fenêtre d'observation : compte les LIST à partir d'ici seulement.
        int listCountAtStart = cluster.TotalListCount;
        var window = Stopwatch.StartNew();

        // Activité réaliste répartie sur la fenêtre : un scale-up, un changement de
        // readiness, une création de job.
        await Task.Delay(500);
        var propagation = Stopwatch.StartNew();
        cluster.SetDeploymentReplicas("fibonacci", 3);
        cluster.AddFunctionPod("fibonacci", "fibonacci-pod-2", "10.0.0.3");
        await KubernetesSyncStack.WaitUntilAsync(
            () => stack.ReplicasService.Deployments.Functions
                .FirstOrDefault(f => f.Deployment == "fibonacci") is { Replicas: 3 },
            "scale-up propagation");
        propagation.Stop();

        await Task.Delay(1000);
        cluster.SetPodReady("fibonacci-pod-0", false);
        await Task.Delay(1000);
        cluster.AddJob("fib-slimfaas-job-0001", "10.0.1.1");

        int remaining = ObservationWindowMilliseconds - (int)window.ElapsedMilliseconds;
        if (remaining > 0)
        {
            await Task.Delay(remaining);
        }

        int listRequests = cluster.TotalListCount - listCountAtStart;
        return new BenchmarkResult(
            listRequests,
            new Dictionary<string, int>(cluster.ListCounts),
            propagation.Elapsed.TotalMilliseconds);
    }
}
