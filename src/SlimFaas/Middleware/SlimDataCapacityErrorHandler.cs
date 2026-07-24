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
        catch (BatchItemTooLargeException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (BatchQueueFullException) when (!context.Response.HasStarted)
        {
            SetTooManyRequests(context);
        }
        catch (FileTransferCapacityExceededException) when (!context.Response.HasStarted)
        {
            SetTooManyRequests(context);
        }
        catch (SlimDataUnavailableException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    private static void SetTooManyRequests(HttpContext context)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = "1";
    }
}
