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
            // Remplacer le display name par le chemin réel de la requête
            var actualPath = httpContext.Request.Path;
            var method = httpContext.Request.Method;

            activity.DisplayName = $"{method} {actualPath}";

            // Ajouter des tags supplémentaires pour le debugging
            activity.SetTag("http.target", actualPath + httpContext.Request.QueryString);
            activity.SetTag("http.route.actual", actualPath.ToString());

            // Conserver aussi le template de route si disponible
            var endpoint = httpContext.GetEndpoint();
            if (endpoint is RouteEndpoint routeEndpoint)
            {
                activity.SetTag("http.route.template", routeEndpoint.RoutePattern.RawText);
            }
        }

        return await next(context);
    }
}
