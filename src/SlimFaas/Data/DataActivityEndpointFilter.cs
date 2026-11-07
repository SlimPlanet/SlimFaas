using Microsoft.Extensions.Options;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Middleware;
using SlimFaas.Options;

namespace SlimFaas;

/// <summary>Observes authorized API calls without reading or buffering their contents.</summary>
public sealed class DataActivityEndpointFilter(NetworkActivityTracker tracker, IJobService jobs,
    IReplicasService replicas, IOptions<SlimFaasOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!tracker.IsEnabled) return await next(context).ConfigureAwait(false);
        var http = context.HttpContext;
        string kind = http.Request.Path.StartsWithSegments("/data/sets", StringComparison.OrdinalIgnoreCase) ? "sets" : "files";
        string operation = Operation(http);
        var caller = FunctionEndpointsHelpers.ResolveNetworkActivityCaller(http, jobs, options.Value.Process.Token, replicas);
        string request = tracker.Record("data_request", caller.Actor, NetworkActivityTracker.Actors.SlimFaas,
            sourcePod: caller.SourcePod, dataKind: kind, dataOperation: operation);
        int status = StatusCodes.Status500InternalServerError;
        try
        {
            var result = await next(context).ConfigureAwait(false);
            status = (result as IStatusCodeHttpResult)?.StatusCode ?? http.Response.StatusCode;
            return result;
        }
        catch (Exception exception) when (!http.Response.HasStarted &&
            SlimDataCapacityErrorHandler.StatusCodeFor(exception) is int mappedStatus)
        {
            status = mappedStatus;
            throw;
        }
        finally
        {
            tracker.Record("data_access", caller.Actor, NetworkActivityTracker.Actors.SlimFaas,
                sourcePod: caller.SourcePod, correlationId: request, dataKind: kind,
                dataOperation: operation, dataStatusCode: status);
        }
    }

    private static string Operation(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method))
            return request.RouteValues.Count == 0 ? "list" : "read";
        if (HttpMethods.IsDelete(request.Method)) return "delete";
        // Numeric operations have an additional literal segment after the set ID.
        if (request.Path.StartsWithSegments("/data/sets", StringComparison.OrdinalIgnoreCase, out var remaining))
        {
            string path = remaining.Value?.TrimEnd('/') ?? string.Empty;
            if (path.Count(character => character == '/') == 2)
                return path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        }
        return "write";
    }
}
