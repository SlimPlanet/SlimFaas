using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;

namespace SlimFaas;

public sealed class DataVisibilityEndpointFilter(
    IOptions<DataOptions> options,
    IFunctionAccessPolicy accessPolicy,
    ILogger<DataVisibilityEndpointFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var vis = options.Value.DefaultVisibility;

        // Public => toujours OK
        if (vis == FunctionVisibility.Public)
            return await next(context);

        // Private => OK seulement si interne
        if (accessPolicy.IsInternalRequest(context.HttpContext))
            return await next(context);

        logger.LogDebug("Denied /data access (DefaultVisibility=Private) for Remote={RemoteIp}",
            context.HttpContext.Connection.RemoteIpAddress?.ToString());

        // même choix que tes fonctions privées : on “cache” => 404
        return Results.NotFound();
    }
}
