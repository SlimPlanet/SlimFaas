using Microsoft.AspNetCore.Http;
using SlimData.ClusterFiles;
using SlimFaas.Middleware;

namespace SlimFaas.Tests.Middleware;

public sealed class SlimDataCapacityErrorHandlerTests
{
    [Fact]
    public async Task File_transfer_capacity_error_returns_429_with_retry_after()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await SlimDataCapacityErrorHandler.InvokeAsync(
            context,
            _ => throw new FileTransferCapacityExceededException("busy"));

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter);
    }
}
