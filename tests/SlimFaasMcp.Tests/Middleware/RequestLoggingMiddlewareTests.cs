using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaasMcp.Middleware;
using Xunit;

namespace SlimFaasMcp.Tests.Middleware;

public class RequestLoggingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenDebugEnabled_LogsRequestWithBody()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<RequestLoggingMiddleware>>();
        loggerMock
            .Setup(l => l.IsEnabled(LogLevel.Debug))
            .Returns(true);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/mcp";
        httpContext.Request.QueryString = new QueryString("?q=test");
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["X-Test"] = "HelloHeader";

        var bodyJson = """{"hello":"world"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;

        bool nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new RequestLoggingMiddleware(next, loggerMock.Object);

        // Act
        await middleware.InvokeAsync(httpContext);

        // Assert
        Assert.True(nextCalled);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("Incoming HTTP") &&
                    v.ToString()!.Contains("/mcp") &&
                    v.ToString()!.Contains(@"""hello"":""world""")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_LargeBody_IsTruncatedInLog()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<RequestLoggingMiddleware>>();
        loggerMock
            .Setup(l => l.IsEnabled(LogLevel.Debug))
            .Returns(true);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/mcp";
        httpContext.Request.ContentType = "application/json";

        // Body très long
        var longText = new string('x', 10000);
        var bodyJson = $"{{\"data\":\"{longText}\"}}";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;

        bool nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new RequestLoggingMiddleware(next, loggerMock.Object);

        // Act
        await middleware.InvokeAsync(httpContext);

        // Assert
        Assert.True(nextCalled);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("Incoming HTTP") &&
                    v.ToString()!.Contains("truncated")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
