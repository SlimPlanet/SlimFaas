using System.Globalization;
using System.Net;
using SlimFaasBenchmark;

namespace SlimFaas.Tests.Benchmark;

public sealed class BenchmarkResourceTests
{
    [Fact]
    public async Task Burst_finishing_before_the_periodic_tick_still_has_initial_and_final_resource_samples()
    {
        using var handler = new ResourceHandler { PostBurstProbeCpuSeconds = 5 };
        using var client = new HttpClient(handler);
        await using var monitoring = await ClusterResourceMonitor.StartAsync(client, [new Uri("http://node/")]);
        handler.CompleteBurst();
        AsyncResourceUsage? resources = await monitoring.CompleteAsync(1000);

        Assert.NotNull(resources);
        Assert.Equal(1, resources.CpuMillisecondsPerMessage);
        Assert.Equal(0.1, resources.RaftEntriesPerMessage);
        Assert.Equal(2048, resources.PeakWorkingSetBytes);
        Assert.NotNull(resources.InitialSnapshot);
        Assert.NotNull(resources.FinalProcessSnapshot);
        Assert.NotNull(resources.FinalDiagnosticsSnapshot);
        Assert.True(resources.FinalDiagnosticsSnapshot.Nodes[0].DiagnosticsCpuSeconds >
            resources.FinalProcessSnapshot.Nodes[0].DiagnosticsCpuSeconds);
        Assert.True(resources.FinalDiagnosticsSnapshot.CpuSeconds > resources.FinalProcessSnapshot.CpuSeconds);
    }

    [Fact]
    public async Task Resource_baseline_waits_for_every_node_instead_of_only_the_aggregate_counter()
    {
        using var handler = new ResourceHandler { HoldLaggingNode = true };
        using var client = new HttpClient(handler);
        Task<ClusterResourceMonitor> starting = ClusterResourceMonitor.StartAsync(client,
            [new Uri("http://fast/"), new Uri("http://lagging/")]);
        await handler.LaggingRead.Task;
        Assert.False(starting.IsCompleted);

        handler.ReleaseLaggingNode();
        await using ClusterResourceMonitor monitoring = await starting;
        handler.CompleteBurst();
        AsyncResourceUsage? resources = await monitoring.CompleteAsync(1000);

        Assert.NotNull(resources);
        Assert.Equal(2, resources.CpuMillisecondsPerMessage);
        Assert.Equal(0.2, resources.RaftEntriesPerMessage);
        Assert.Equal(2, resources.InitialSnapshot!.Nodes.Count);
    }

    [Fact]
    public async Task Missing_live_process_counter_fails_before_workload_start()
    {
        using var handler = new ResourceHandler { OmitLiveCpu = true };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClusterResourceMonitor.StartAsync(client, [new Uri("http://node/")]));
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("raft")]
    [InlineData("diagnostics")]
    [InlineData("identity")]
    public async Task One_node_reset_cannot_be_hidden_by_other_nodes_progress(string reset)
    {
        using var handler = new ResourceHandler { ResetCounter = reset };
        using var client = new HttpClient(handler);
        ClusterResourceMonitor monitoring = await ClusterResourceMonitor.StartAsync(client,
            [new Uri("http://fast/"), new Uri("http://resetting/")]);
        handler.CompleteBurst();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => monitoring.CompleteAsync(1000));
        }
        finally
        {
            // Completion errors are retained by the monitor and replayed during disposal.
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await monitoring.DisposeAsync());
        }
    }

    private sealed class ResourceHandler : HttpMessageHandler
    {
        private int _completed;
        private int _reads;
        private int _laggingReads;
        private int _released;
        private int _postBurstReads;
        internal bool HoldLaggingNode { get; init; }
        internal bool OmitLiveCpu { get; init; }
        internal double PostBurstProbeCpuSeconds { get; init; }
        internal string? ResetCounter { get; init; }
        internal TaskCompletionSource LaggingRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CompleteBurst() => Interlocked.Exchange(ref _completed, 1);
        internal void ReleaseLaggingNode() => Interlocked.Exchange(ref _released, 1);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int completed = Volatile.Read(ref _completed);
            int postBurstReads = completed == 0 ? 0 : Interlocked.Increment(ref _postBurstReads);
            double liveCpu = 10 + completed + Math.Max(0, postBurstReads - 1) * PostBurstProbeCpuSeconds;
            int sequence = Interlocked.Increment(ref _reads);
            bool lagging = request.RequestUri!.Host == "lagging";
            if (lagging && Interlocked.Increment(ref _laggingReads) >= 2)
                LaggingRead.TrySetResult();
            double diagnosticsCpu = HoldLaggingNode && lagging && Volatile.Read(ref _released) == 0
                ? 0 : sequence / 100d;
            double raftEntries = 1000 + 100 * completed;
            double processStart = 100;
            if (ResetCounter is not null && completed != 0)
            {
                bool resetting = request.RequestUri.Host == "resetting";
                liveCpu = resetting && ResetCounter == "cpu" ? 1 : 30;
                raftEntries = resetting && ResetCounter == "raft" ? 1 : 3000;
                diagnosticsCpu = resetting && ResetCounter == "diagnostics" ? 0 : diagnosticsCpu;
                processStart = resetting && ResetCounter == "identity" ? 200 : processStart;
            }
            string liveCpuName = OmitLiveCpu ? "unrelated_cpu" : "process_cpu_seconds_total";
            string metrics = string.Create(CultureInfo.InvariantCulture, $"""
                {liveCpuName} {liveCpu}
                process_start_time_seconds {processStart}
                process_working_set_bytes {1024 + 1024 * completed}
                slimdata_process_cpu_seconds_total {diagnosticsCpu}
                slimdata_command_batch_raft_total {raftEntries}
                slimdata_process_working_set_bytes {1024 + 1024 * completed}
                """);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(metrics)
            });
        }
    }
}
