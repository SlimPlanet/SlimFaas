using SlimData;
using SlimData.ClusterFiles;

namespace SlimFaas.Middleware;

internal static class SlimDataCapacityErrorHandler
{
    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (!context.Response.HasStarted && StatusCodeFor(exception) is int status)
        {
            context.Response.Clear();
            context.Response.StatusCode = status;
            if (status == StatusCodes.Status429TooManyRequests)
                context.Response.Headers.RetryAfter = "1";
        }
    }

    internal static int? StatusCodeFor(Exception exception) => exception switch
    {
        BatchItemTooLargeException => StatusCodes.Status413PayloadTooLarge,
        BatchQueueFullException or FileTransferCapacityExceededException => StatusCodes.Status429TooManyRequests,
        SlimDataUnavailableException => StatusCodes.Status503ServiceUnavailable,
        _ => null
    };
}
