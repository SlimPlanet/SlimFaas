using System.Diagnostics;
using SlimFaas.Endpoints;

namespace SlimFaas.Tests.Endpoints;

public class OpenTelemetryGlobalRouteRewriteProcessorTests
{
    private static Activity StartServerActivity(out ActivitySource source, out ActivityListener listener, string name = "incoming")
    {
        listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };

        ActivitySource.AddActivityListener(listener);
        source = new ActivitySource("SlimFaas.Tests");

        var activity = source.StartActivity(name, ActivityKind.Server);
        Assert.NotNull(activity);
        return activity!;
    }

    [Fact]
    public void OnEnd_ServerSpan_RewritesHttpRoute_AndSetsDisplayName_AndStoresTemplate()
    {
        // Arrange
        var processor = new OpenTelemetryGlobalRouteRewriteProcessor();

        using var activity = StartServerActivity(out var source, out var listener);
        using (source)
        using (listener)
        {
            activity.SetTag("url.path", "/function/fibonacci/compute");
            activity.SetTag("http.request.method", "POST");
            activity.SetTag("http.route", "/function/{functionName}/{**functionPath}");

            activity.Stop();

            // Act
            processor.OnEnd(activity);

            // Assert
            Assert.Equal("/function/fibonacci/compute", activity.GetTagItem("http.route") as string);
            Assert.Equal("/function/{functionName}/{**functionPath}", activity.GetTagItem("http.route.template") as string);
            Assert.Equal("POST /function/fibonacci/compute", activity.DisplayName);
        }
    }

    [Fact]
    public void OnEnd_ServerSpan_WhenUrlPathMissing_UsesHttpTargetWithoutQuery()
    {
        // Arrange
        var processor = new OpenTelemetryGlobalRouteRewriteProcessor();

        using var activity = StartServerActivity(out var source, out var listener);
        using (source)
        using (listener)
        {
            activity.SetTag("http.target", "/function/foo/bar?x=1&y=2");
            activity.SetTag("http.method", "GET"); // fallback method key
            activity.SetTag("http.route", "/function/{functionName}/{**functionPath}");

            activity.Stop();

            // Act
            processor.OnEnd(activity);

            // Assert
            Assert.Equal("/function/foo/bar", activity.GetTagItem("http.route") as string);
            Assert.Equal("/function/{functionName}/{**functionPath}", activity.GetTagItem("http.route.template") as string);
            Assert.Equal("GET /function/foo/bar", activity.DisplayName);
        }
    }

    [Fact]
    public void OnEnd_ServerSpan_DoesNotSetTemplate_WhenPreviousRouteIsNullOrSame()
    {
        // Arrange
        var processor = new OpenTelemetryGlobalRouteRewriteProcessor();

        using var activity = StartServerActivity(out var source, out var listener);
        using (source)
        using (listener)
        {
            activity.SetTag("url.path", "/healthz");
            activity.SetTag("http.request.method", "GET");
            // http.route absent => previous null

            activity.Stop();

            // Act
            processor.OnEnd(activity);

            // Assert
            Assert.Equal("/healthz", activity.GetTagItem("http.route") as string);
            Assert.Null(activity.GetTagItem("http.route.template"));
            Assert.Equal("GET /healthz", activity.DisplayName);
        }
    }

    [Fact]
    public void OnEnd_NonServerSpan_IsNotModified()
    {
        // Arrange
        var processor = new OpenTelemetryGlobalRouteRewriteProcessor();

        // create Client activity
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("SlimFaas.Tests");
        using var activity = source.StartActivity("outgoing", ActivityKind.Client)!;

        activity.SetTag("url.path", "/function/should/not/change");
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.route", "ORIGINAL");

        activity.Stop();

        // Act
        processor.OnEnd(activity);

        // Assert
        Assert.Equal("ORIGINAL", activity.GetTagItem("http.route") as string);
        Assert.Null(activity.GetTagItem("http.route.template"));
    }
}
