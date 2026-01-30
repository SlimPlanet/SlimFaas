using System.Diagnostics;
using OpenTelemetry;

namespace SlimFaas.Endpoints;

public sealed class OpenTelemetryGlobalRouteRewriteProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity.Kind != ActivityKind.Server)
            return;

        var actualPath = activity.GetTagItem("url.path") as string;

        if (string.IsNullOrEmpty(actualPath))
        {
            var target = activity.GetTagItem("http.target") as string;
            if (!string.IsNullOrEmpty(target))
                actualPath = target.Split('?', 2)[0];
        }

        if (string.IsNullOrEmpty(actualPath))
            return;

        var method =
            activity.GetTagItem("http.request.method") as string ??
            activity.GetTagItem("http.method") as string ??
            "HTTP";

        var previous = activity.GetTagItem("http.route") as string;
        if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, actualPath, StringComparison.Ordinal))
        {
            activity.SetTag("http.route.template", previous);
        }

        activity.SetTag("http.route", actualPath);

        activity.DisplayName = $"{method} {actualPath}";
    }
}
