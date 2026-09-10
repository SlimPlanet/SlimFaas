using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.RateLimiting;

namespace SlimFaas.Tests.RateLimiting;

public class CpuRateLimitingMiddlewareTests
{
    private static readonly int[] ExcludedPorts = [8080, 8081];

    private static IOptions<RateLimitingOptions> CreateOptions(RateLimitingOptions options)
    {
        var mock = new Mock<IOptions<RateLimitingOptions>>();
        mock.Setup(x => x.Value).Returns(options);
        return mock.Object;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenCpuRecoversBetweenFunctionCalls_ResumesOnSameMiddleware(bool callProbes)
    {
        var options = CreateOptions(new RateLimitingOptions());
        var metrics = new CpuMetrics(options);
        var forwarded = 0;
        var middleware = new CpuRateLimitingMiddleware(
            _ => { forwarded++; return Task.CompletedTask; },
            options, metrics, ExcludedPorts);

        async Task<int> Request(string path)
        {
            var context = new DefaultHttpContext
            {
                Connection = { LocalPort = 5000 },
                Request = { Path = path },
                Response = { Body = new MemoryStream() }
            };
            await middleware.InvokeAsync(context);
            return context.Response.StatusCode;
        }

        metrics.UpdateCpuUsage(85);
        Assert.Equal(429, await Request("/function/demo/hello"));
        metrics.UpdateCpuUsage(50);
        if (callProbes)
        {
            Assert.Equal(200, await Request("/health"));
            Assert.Equal(200, await Request("/ready"));
        }
        metrics.UpdateCpuUsage(70);
        Assert.Equal(200, await Request("/function/demo/hello"));
        Assert.Equal(callProbes ? 3 : 1, forwarded);
    }

    [Fact]
    public async Task InvokeAsync_WhenDisabled_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions { Enabled = false });
        var cpuProvider = new Mock<ICpuMetrics>();
        cpuProvider.SetupGet(p => p.IsLimiting).Returns(true);
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, ExcludedPorts);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenExcludedPort_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true, CpuHighThreshold = 80, CpuLowThreshold = 60
        });
        var cpuProvider = new Mock<ICpuMetrics>();
        cpuProvider.SetupGet(p => p.IsLimiting).Returns(true);
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, ExcludedPorts);
        var context = new DefaultHttpContext { Connection = { LocalPort = 8080 } };

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenSecondExcludedPort_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true, CpuHighThreshold = 80, CpuLowThreshold = 60
        });
        var cpuProvider = new Mock<ICpuMetrics>();
        cpuProvider.SetupGet(p => p.IsLimiting).Returns(true);
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, ExcludedPorts);
        var context = new DefaultHttpContext { Connection = { LocalPort = 8081 } };

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenExcludedPath_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            ExcludedPaths = ["/health"]
        });
        var cpuProvider = new Mock<ICpuMetrics>();
        cpuProvider.Setup(p => p.IsLimiting).Returns(true);
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, ExcludedPorts);
        var context = new DefaultHttpContext { Connection = { LocalPort = 5000 }, Request = { Path = "/health" } };

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenExcludedPathIsSlimDataSubroute_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            ExcludedPaths = ["/SlimData"]
        });
        var cpuProvider = new Mock<ICpuMetrics>();
        cpuProvider.Setup(p => p.IsLimiting).Returns(true);
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, ExcludedPorts);
        var context = new DefaultHttpContext
        {
            Connection = { LocalPort = 5000 },
            Request = { Path = "/SlimData/protocol" }
        };

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenCpuHigh_Returns429()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            RetryAfterSeconds = 5
        });
        var cpuProvider = new Mock<ICpuMetrics>();
        cpuProvider.Setup(p => p.IsLimiting).Returns(true);
        RequestDelegate next = _ => throw new InvalidOperationException("A rejected request must not be forwarded.");

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, ExcludedPorts);
        var context = new DefaultHttpContext
        {
            Connection = { LocalPort = 5000 },
            Request = { Path = "/api/test" },
            Response = { Body = new MemoryStream() }
        };

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.True(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("5", context.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task InvokeAsync_WithHysteresis_MaintainsLimitingState()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true, CpuHighThreshold = 80, CpuLowThreshold = 60
        });
        var cpuProvider = new CpuMetrics(options);
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider, ExcludedPorts);

        cpuProvider.UpdateCpuUsage(85);
        var contextWithCpuGreaterThanHighThreshold =
            new DefaultHttpContext { Connection = { LocalPort = 5000 }, Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(contextWithCpuGreaterThanHighThreshold);
        Assert.Equal(429, contextWithCpuGreaterThanHighThreshold.Response.StatusCode);

        cpuProvider.UpdateCpuUsage(70);
        var contextWithCpuGreaterThanLowThreshold =
            new DefaultHttpContext { Connection = { LocalPort = 5000 }, Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(contextWithCpuGreaterThanLowThreshold);
        Assert.Equal(429, contextWithCpuGreaterThanLowThreshold.Response.StatusCode);

        Assert.False(nextCalled);
        cpuProvider.UpdateCpuUsage(50);

        var contextWithCpuLesserThanLowThreshold =
            new DefaultHttpContext { Connection = { LocalPort = 5000 }, Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(contextWithCpuLesserThanLowThreshold);
        Assert.Equal(200, contextWithCpuLesserThanLowThreshold.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/ready", 200)]
    [InlineData("/metrics", 200)]
    [InlineData("/SlimData/CommandBatch", 200)]
    [InlineData("/slimdata/protocol", 200)]
    [InlineData("/SlimDatabase", 429)]
    [InlineData("/status-functions", 429)]
    [InlineData("/function/demo/hello", 429)]
    public async Task InvokeAsync_WhenLimiting_RespectsDefaultPathExclusions(string path, int expected)
    {
        var options = CreateOptions(new RateLimitingOptions { RetryAfterSeconds = null });
        var metrics = new CpuMetrics(options);
        metrics.UpdateCpuUsage(85);
        var middleware = new CpuRateLimitingMiddleware(_ => Task.CompletedTask, options, metrics, ExcludedPorts);
        var context = new DefaultHttpContext
        {
            Connection = { LocalPort = 5000 },
            Request = { Path = path },
            Response = { Body = new MemoryStream() }
        };

        await middleware.InvokeAsync(context);

        Assert.Equal(expected, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task InvokeAsync_ConcurrentRequests_ObserveRecoveryWithoutRestart()
    {
        var options = CreateOptions(new RateLimitingOptions());
        var metrics = new CpuMetrics(options);
        var forwarded = 0;
        var middleware = new CpuRateLimitingMiddleware(
            _ => { Interlocked.Increment(ref forwarded); return Task.CompletedTask; },
            options, metrics, ExcludedPorts);

        Task<int[]> Requests() => Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            var context = new DefaultHttpContext
            {
                Connection = { LocalPort = 5000 },
                Request = { Path = "/function/demo/hello" },
                Response = { Body = new MemoryStream() }
            };
            await middleware.InvokeAsync(context);
            return context.Response.StatusCode;
        })));

        metrics.UpdateCpuUsage(85);
        Assert.All(await Requests(), status => Assert.Equal(429, status));
        metrics.UpdateCpuUsage(50);
        metrics.UpdateCpuUsage(70);
        Assert.All(await Requests(), status => Assert.Equal(200, status));
        Assert.Equal(64, forwarded);
    }

    [Fact]
    public async Task InvokeAsync_WhenEmptyExcludedPorts_AppliesRateLimiting()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60
        });
        var cpuProvider = new Mock<ICpuMetrics>();
        cpuProvider.Setup(p => p.IsLimiting).Returns(true);
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, Array.Empty<int>());
        var context = new DefaultHttpContext
        {
            Connection = { LocalPort = 8080 },
            Response = { Body = new MemoryStream() }
        };

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenCpuLow_CallsNext()
    {
        var options = CreateOptions(new RateLimitingOptions
        {
            Enabled = true, CpuHighThreshold = 80, CpuLowThreshold = 60
        });
        var cpuProvider = new Mock<ICpuMetrics>();
        cpuProvider.Setup(p => p.IsLimiting).Returns(false);
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CpuRateLimitingMiddleware(next, options, cpuProvider.Object, ExcludedPorts);
        var context = new DefaultHttpContext { Connection = { LocalPort = 5000 } };

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }
}
