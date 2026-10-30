using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimData;
using SlimFaas.Endpoints;
using SlimFaas.Options;

namespace SlimFaas.Streams;

internal static class StreamObservation
{
    public static bool Visible(HttpContext context, NetworkActivityEvent activity) =>
        activity.StreamName is null || context.RequestServices.GetService<IStreamService>()?.CanObserveActivity(context, activity) == true;

    public static async Task<string> ProjectAsync(HttpContext context, string frame, CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetService<IStreamService>();
        if (service is null) return frame;
        int start = frame.IndexOf("data: ", StringComparison.Ordinal) + 6;
        var original = JsonSerializer.Deserialize(frame.AsSpan(start).Trim(), StatusStreamSerializerContext.Default.StatusStreamPayload)
            ?? throw new InvalidDataException("Missing status snapshot.");
        bool enabled = context.RequestServices.GetRequiredService<IOptions<SlimFaasOptions>>().Value.EnableFront;
        var payload = await service.ReadAsync(cancellationToken).ConfigureAwait(false);
        var all = enabled ? service.Summaries(context, payload) : [];
        var summaries = all.Take(500).ToArray();
        var functions = summaries.SelectMany(summary => service.Functions(context, payload,
            DurableStreamStore.Definition(payload, summary.Name)!)).Take(501).ToArray();
        var subscriptions = functions.Take(500).ToArray();
        var projected = original with
        {
            RecentActivity = original.RecentActivity.Where(item => Visible(context, item)).ToArray(),
            Streams = summaries, StreamSubscriptions = subscriptions, StreamsAvailable = enabled,
            StreamsTruncated = all.Count > 500 || functions.Length > 500
        };
        return "event: state\ndata: " + JsonSerializer.Serialize(projected, StatusStreamSerializerContext.Default.StatusStreamPayload) + "\n\n";
    }
}
