using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.RateLimiting;

namespace SlimFaas.Tests.RateLimiting;

public class CpuRateLimitingMiddlewareTests
{
    private static IOptions<RateLimitingOptions> CreateOptions(RateLimitingOptions options)
    {
        var mock = new Mock<IOptions<RateLimitingOptions>>();
        mock.Setup(x => x.Value).Returns(options);
        return mock.Object;
    }

    [Fact]
    public async Task InvokeAsync_WhenDisabled_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions { Enabled = false });
        var cpuProvider = new Mock<ICpuUsageProvider>();
        var logger = new Mock<ILogger<CpuRateLimitingMiddleware>>();
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, logger.Object);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenNotPublicPort_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true, PublicPort = 5000, CpuHighThreshold = 80, CpuLowThreshold = 60
        });
        var cpuProvider = new Mock<ICpuUsageProvider>();
        var logger = new Mock<ILogger<CpuRateLimitingMiddleware>>();
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, logger.Object);
        var context = new DefaultHttpContext { Connection = { LocalPort = 8080 } };

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenExcludedPath_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true,
            PublicPort = 5000,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            ExcludedPaths = ["/health"]
        });
        var cpuProvider = new Mock<ICpuUsageProvider>();
        cpuProvider.Setup(p => p.CurrentCpuPercent).Returns(90);
        var logger = new Mock<ILogger<CpuRateLimitingMiddleware>>();
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, logger.Object);
        var context = new DefaultHttpContext { Connection = { LocalPort = 5000 }, Request = { Path = "/health" } };

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenCpuHigh_Returns429()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true,
            PublicPort = 5000,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            StatusCode = 429,
            RetryAfterSeconds = 5
        });
        var cpuProvider = new Mock<ICpuUsageProvider>();
        cpuProvider.Setup(p => p.CurrentCpuPercent).Returns(85);
        var logger = new Mock<ILogger<CpuRateLimitingMiddleware>>();
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, logger.Object);
        var context = new DefaultHttpContext
        {
            Connection = { LocalPort = 5000 },
            Request = { Path = "/api/test" },
            Response = { Body = new MemoryStream() }
        };

        await middleware.InvokeAsync(context);

        Assert.Equal(429, context.Response.StatusCode);
        Assert.True(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("5", context.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task InvokeAsync_WithHysteresis_MaintainsLimitingState()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true, PublicPort = 5000, CpuHighThreshold = 80, CpuLowThreshold = 60
        });
        var cpuProvider = new Mock<ICpuUsageProvider>();
        var logger = new Mock<ILogger<CpuRateLimitingMiddleware>>();
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, logger.Object);

        cpuProvider.Setup(p => p.CurrentCpuPercent).Returns(85);
        var contextWithCpuGreaterThanHighThreshold =
            new DefaultHttpContext { Connection = { LocalPort = 5000 }, Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(contextWithCpuGreaterThanHighThreshold);
        Assert.Equal(429, contextWithCpuGreaterThanHighThreshold.Response.StatusCode);

        cpuProvider.Setup(p => p.CurrentCpuPercent).Returns(70);
        var contextWithCpuGreaterThanLowThreshold =
            new DefaultHttpContext { Connection = { LocalPort = 5000 }, Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(contextWithCpuGreaterThanLowThreshold);
        Assert.Equal(429, contextWithCpuGreaterThanLowThreshold.Response.StatusCode);

        cpuProvider.Setup(p => p.CurrentCpuPercent).Returns(50);
        bool nextCalled = false;
        RequestDelegate nextWithCheck = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware2 = new CpuRateLimitingMiddleware(nextWithCheck, options, cpuProvider.Object, logger.Object);

        var contextWithCpuLesserThanLowThreshold =
            new DefaultHttpContext { Connection = { LocalPort = 5000 }, Response = { Body = new MemoryStream() } };

        await middleware2.InvokeAsync(contextWithCpuLesserThanLowThreshold);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenCpuLow_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true, PublicPort = 5000, CpuHighThreshold = 80, CpuLowThreshold = 60
        });
        var cpuProvider = new Mock<ICpuUsageProvider>();
        cpuProvider.Setup(p => p.CurrentCpuPercent).Returns(50);
        var logger = new Mock<ILogger<CpuRateLimitingMiddleware>>();
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, logger.Object);
        var context = new DefaultHttpContext { Connection = { LocalPort = 5000 } };

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }
}
