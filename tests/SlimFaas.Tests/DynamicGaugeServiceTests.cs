using Prometheus;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Tests;

public sealed class DynamicGaugeServiceTests
{
    [Fact]
    public async Task RemovedLabelValuesAreNotRetainedByPrometheus()
    {
        var registry = Metrics.NewCustomRegistry();
        var gauges = new DynamicGaugeService(Metrics.WithCustomRegistry(registry));
        var labels = new Dictionary<string, string> { ["function"] = "deleted-function" };

        gauges.SetGaugeValue(
            "slimfaas_test_queue_items",
            1,
            "Test queue length",
            labels);
        Assert.Contains("deleted-function", await ExportAsync(registry), StringComparison.Ordinal);

        gauges.RemoveGaugeValue("slimfaas_test_queue_items", labels);

        Assert.DoesNotContain("deleted-function", await ExportAsync(registry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsWorkerRemovesDeletedFunctionSeries()
    {
        var registry = Metrics.NewCustomRegistry();
        var gauges = new DynamicGaugeService(Metrics.WithCustomRegistry(registry));
        const string function = "deleted-function";
        var labels = new Dictionary<string, string> { ["function"] = function };
        string[] metricNames =
        [
            "slimfaas_function_queue_ready_items",
            "slimfaas_function_queue_in_flight_items",
            "slimfaas_function_queue_retry_pending_items"
        ];
        foreach (var metricName in metricNames)
        {
            gauges.SetGaugeValue(metricName, 1, "Test queue metric", labels);
        }

        var worker = new MetricsWorker(
            Mock.Of<IReplicasService>(),
            Mock.Of<ISlimFaasQueue>(),
            gauges,
            Mock.Of<IRaftCluster>(),
            Mock.Of<ILogger<MetricsWorker>>(),
            Microsoft.Extensions.Options.Options.Create(new WorkersOptions()));
        var deployment = new DeploymentInformation(
            function,
            "default",
            [],
            new SlimFaasConfiguration(),
            Replicas: 1);

        worker.RemoveStaleFunctionMetrics([deployment]);
        worker.RemoveStaleFunctionMetrics([]);

        string exposition = await ExportAsync(registry);
        Assert.DoesNotContain(function, exposition, StringComparison.Ordinal);
        Assert.All(metricNames, name => Assert.DoesNotContain(name + "{", exposition, StringComparison.Ordinal));
    }

    private static async Task<string> ExportAsync(CollectorRegistry registry)
    {
        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        return System.Text.Encoding.UTF8.GetString(
            stream.GetBuffer(),
            0,
            checked((int)stream.Length));
    }
}
