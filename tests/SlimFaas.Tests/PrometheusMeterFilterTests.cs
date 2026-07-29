using System.Diagnostics.Metrics;
using System.Text;
using Prometheus;

namespace SlimFaas.Tests;

public sealed class PrometheusMeterFilterTests
{
    [Theory]
    [InlineData("System.Net.Http")]
    [InlineData("Microsoft.AspNetCore.Hosting")]
    public void ExcludesDuplicatedHttpMeters(string meterName)
    {
        using var meter = new Meter(meterName);
        var instrument = meter.CreateCounter<long>("requests");

        Assert.False(PrometheusMeterFilter.ShouldPublish(instrument));
    }

    [Theory]
    [InlineData("System.Runtime")]
    [InlineData("DotNext.Net.Cluster.Consensus.Raft")]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel")]
    [InlineData("Microsoft.AspNetCore.MemoryPool")]
    [InlineData("System.Net.NameResolution")]
    public void PreservesNonDuplicatedMeters(string meterName)
    {
        using var meter = new Meter(meterName);
        var instrument = meter.CreateCounter<long>("measurements");

        Assert.True(PrometheusMeterFilter.ShouldPublish(instrument));
    }

    [Fact]
    public async Task FilteredAdapterExportsRuntimeButNotDuplicatedHttpMeters()
    {
        var registry = Metrics.NewCustomRegistry();
        var factory = Metrics.WithCustomRegistry(registry);
        using var httpClientMeter = new Meter("System.Net.Http");
        using var aspNetCoreMeter = new Meter("Microsoft.AspNetCore.Hosting");
        using var runtimeMeter = new Meter("System.Runtime");
        var httpClientHistogram = httpClientMeter.CreateHistogram<double>("request.duration");
        var aspNetCoreHistogram = aspNetCoreMeter.CreateHistogram<double>("request.duration");
        var runtimeCounter = runtimeMeter.CreateCounter<long>("slimfaas.filter.test");

        using var adapter = MeterAdapter.StartListening(new MeterAdapterOptions
        {
            InstrumentFilterPredicate = instrument =>
                (instrument.Meter == httpClientMeter ||
                 instrument.Meter == aspNetCoreMeter ||
                 instrument.Meter == runtimeMeter) &&
                PrometheusMeterFilter.ShouldPublish(instrument),
            Registry = registry,
            MetricFactory = factory
        });

        httpClientHistogram.Record(0.1, new KeyValuePair<string, object?>("server.address", "10.0.0.1"));
        aspNetCoreHistogram.Record(0.2, new KeyValuePair<string, object?>("http.route", "/function/{name}"));
        runtimeCounter.Add(1);

        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        var exposition = Encoding.UTF8.GetString(stream.ToArray());

        Assert.DoesNotContain("system_net_http_", exposition, StringComparison.Ordinal);
        Assert.DoesNotContain("microsoft_aspnetcore_hosting_", exposition, StringComparison.Ordinal);
        Assert.Contains("system_runtime_slimfaas_filter_test", exposition, StringComparison.Ordinal);
    }
}
