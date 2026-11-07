using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;

namespace SlimFaas.Endpoints;

internal static class DataObservation
{
    internal static bool CanObserve(HttpContext context)
    {
        var settings = context.RequestServices.GetRequiredService<IOptions<SlimFaasOptions>>().Value;
        var data = context.RequestServices.GetRequiredService<IOptions<DataOptions>>().Value;
        return settings.EnableFront && settings.ExposeDataMetadata ||
               data.DefaultVisibility == FunctionVisibility.Public ||
               context.RequestServices.GetService<IFunctionAccessPolicy>()?.IsInternalRequest(context) == true;
    }

    internal static bool Visible(HttpContext context, NetworkActivityEvent activity) =>
        activity.DataKind is null || CanObserve(context);

    internal static StatusStreamPayload Project(HttpContext context, StatusStreamPayload payload)
    {
        bool accessible = CanObserve(context);
        bool includeActivity = !string.Equals(context.Request.Query["activity"], "false", StringComparison.OrdinalIgnoreCase);
        return payload with
        {
            DataSummary = accessible && includeActivity
                ? context.RequestServices.GetService<DataStatusSnapshotCache>()?.GetSummary() : null,
            RecentActivity = payload.RecentActivity.Where(item => item.DataKind is null || accessible).ToArray()
        };
    }
}
