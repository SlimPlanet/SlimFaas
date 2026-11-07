using System.Text.Json;
using SlimData;
using SlimFaas.Endpoints;

namespace SlimFaas.Streams;

internal static class StreamObservation
{
    public static bool Visible(HttpContext context, NetworkActivityEvent activity) =>
        activity.StreamName is null || context.RequestServices.GetService<IStreamService>()?.CanObserveActivity(context, activity) == true;

    public static async Task<string> ProjectAsync(HttpContext context, string frame, CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetService<IStreamService>();
        int start = frame.IndexOf("data: ", StringComparison.Ordinal) + 6;
        var original = JsonSerializer.Deserialize(frame.AsSpan(start).Trim(), StatusStreamSerializerContext.Default.StatusStreamPayload)
            ?? throw new InvalidDataException("Missing status snapshot.");
        bool enabled = service?.FrontEnabled == true;
        bool accessible = service?.CanManage(context) == true;
        StreamSummary[] summaries = [];
        StreamFunctionSummary[] subscriptions = [];
        bool truncated = false;
        if (enabled && accessible && service is not null)
        {
            var payload = await service.ReadAsync(cancellationToken).ConfigureAwait(false);
            var all = service.Summaries(context, payload);
            summaries = all.Take(500).ToArray();
            var functions = summaries.SelectMany(summary => service.Functions(context, payload,
                DurableStreamStore.Definition(payload, summary.Name)!)).Take(501).ToArray();
            subscriptions = functions.Take(500).ToArray();
            truncated = all.Count > 500 || functions.Length > 500;
        }
        var projected = original with
        {
            RecentActivity = original.RecentActivity.Where(item => Visible(context, item)).ToArray(),
            Streams = summaries, StreamSubscriptions = subscriptions, StreamsAvailable = enabled && accessible,
            StreamsTruncated = truncated, StreamsFrontEnabled = enabled, StreamsAccessible = accessible
        };
        return "event: state\ndata: " + JsonSerializer.Serialize(DataObservation.Project(context, projected), StatusStreamSerializerContext.Default.StatusStreamPayload) + "\n\n";
    }
}
