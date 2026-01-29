using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using SlimFaas.Endpoints;

namespace SlimFaas.Tests.Endpoints;

public class OpenTelemetryEnrichmentFilterTests
{
    [Fact]
    public async Task InvokeAsync_ShouldEnrichActivity_WithActualPath()
    {
        // Arrange
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activitySource = new ActivitySource("TestSource");
        using var activity = activitySource.StartActivity();

        var filter = new OpenTelemetryEnrichmentFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/function/fibonacci/compute";
        httpContext.Request.QueryString = new QueryString("?n=10");
        httpContext.Request.Method = "GET";

        var context = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        // Act
        await filter.InvokeAsync(context, next);

        // Assert
        Assert.True(nextCalled);
        Assert.NotNull(activity);
        Assert.Equal("GET /function/fibonacci/compute", activity.DisplayName);
    }

    [Fact]
    public async Task InvokeAsync_ShouldWork_WhenActivityIsNull()
    {
        // Arrange
        var filter = new OpenTelemetryEnrichmentFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/function/fibonacci/compute";
        httpContext.Request.Method = "POST";

        var context = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        // Act
        await filter.InvokeAsync(context, next);

        // Assert
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldAddRouteTemplate_WhenAvailable()
    {
        // Arrange
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activitySource = new ActivitySource("TestSource");
        using var activity = activitySource.StartActivity();

        var filter = new OpenTelemetryEnrichmentFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/function/fibonacci/compute";
        httpContext.Request.Method = "GET";

        // Créer un RouteEndpoint avec un pattern
        var routePattern = RoutePatternFactory.Parse("/function/{functionName}/{**functionPath}");
        var routeEndpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            routePattern,
            0,
            EndpointMetadataCollection.Empty,
            "TestEndpoint");

        httpContext.SetEndpoint(routeEndpoint);

        var context = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        // Act
        await filter.InvokeAsync(context, next);

        // Assert
        Assert.True(nextCalled);
        Assert.NotNull(activity);
        Assert.Equal("/function/{functionName}/{**functionPath}", activity.GetTagItem("http.route.template"));
    }

    [Theory]
    [InlineData("GET", "/function/test/path")]
    [InlineData("POST", "/async-function/worker/compute")]
    [InlineData("PUT", "/publish-event/reload")]
    [InlineData("DELETE", "/function/cleanup/resources")]
    public async Task InvokeAsync_ShouldHandleDifferentMethods_AndPaths(string method, string path)
    {
        // Arrange
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activitySource = new ActivitySource("TestSource");
        using var activity = activitySource.StartActivity();

        var filter = new OpenTelemetryEnrichmentFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Request.Method = method;

        var context = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        // Act
        await filter.InvokeAsync(context, next);

        // Assert
        Assert.True(nextCalled);
        Assert.NotNull(activity);
        Assert.Equal($"{method} {path}", activity.DisplayName);
        Assert.Equal(path, activity.GetTagItem("http.route"));
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_AndReturnResult()
    {
        // Arrange
        var filter = new OpenTelemetryEnrichmentFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/test";
        httpContext.Request.Method = "GET";

        var context = new DefaultEndpointFilterInvocationContext(httpContext);
        var expectedResult = Results.Ok("Success");
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(expectedResult);

        // Act
        var result = await filter.InvokeAsync(context, next);

        // Assert
        Assert.Equal(expectedResult, result);
    }
}
