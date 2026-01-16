namespace SlimFaas.Endpoints;

/// <summary>
/// Filtre d'endpoint qui vérifie si le port de la requête correspond aux ports SlimFaas configurés.
/// Si le port ne correspond pas, la requête est ignorée (retourne NotFound).
/// </summary>
public class HostPortEndpointFilter : IEndpointFilter
{
    private readonly ISlimFaasPorts? _slimFaasPorts;

    public HostPortEndpointFilter(ISlimFaasPorts? slimFaasPorts = null)
    {
        _slimFaasPorts = slimFaasPorts;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        // Vérifie si le port de la connexion locale ou le port de l'hôte correspond aux ports SlimFaas
        if (!HostPort.IsSamePort(
            [httpContext.Connection.LocalPort, httpContext.Request.Host.Port ?? 0],
            _slimFaasPorts?.Ports.ToArray() ?? []))
        {
            // Si le port ne correspond pas, on ignore la requête
            return Results.NotFound();
        }

        // Sinon, on continue le traitement de la requête
        return await next(context);
    }
}

