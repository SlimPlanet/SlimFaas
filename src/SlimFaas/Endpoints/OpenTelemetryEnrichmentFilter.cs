using System.Diagnostics;

namespace SlimFaas.Endpoints;

/// <summary>
/// Endpoint filter qui enrichit les traces OpenTelemetry avec le chemin réel de la requête
/// au lieu du template de route (ex: /function/fibonacci/compute au lieu de /function/{functionName}/{**functionPath})
/// </summary>
public class OpenTelemetryEnrichmentFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var activity = Activity.Current;

        if (activity != null)
        {
            var actualPath = httpContext.Request.Path;
            var method = httpContext.Request.Method;

            activity.DisplayName = $"{method} {actualPath}";

            activity.SetTag("http.route", actualPath.ToString());

            var endpoint = httpContext.GetEndpoint();
            if (endpoint is RouteEndpoint routeEndpoint)
            {
                activity.SetTag("http.route.template", routeEndpoint.RoutePattern.RawText);
            }
        }

        return await next(context);
    }
}
