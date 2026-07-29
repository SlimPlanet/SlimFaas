using System.Diagnostics.Metrics;
using Prometheus;

namespace SlimFaas;

internal static class PrometheusMeterFilter
{
    internal static void ConfigureDefaultAdapter()
    {
        Metrics.ConfigureMeterAdapter(options =>
            options.InstrumentFilterPredicate = ShouldPublish);
    }

    internal static bool ShouldPublish(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        return instrument.Meter.Name is not "System.Net.Http"
            and not "Microsoft.AspNetCore.Hosting";
    }
}
