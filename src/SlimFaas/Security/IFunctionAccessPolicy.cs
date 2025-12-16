using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Security;

public interface IFunctionAccessPolicy
{
    // True = appel venant d’un pod “trusted” / job (donc interne)
    bool IsInternalRequest(HttpContext context);

    // Décision finale : public => OK, private => OK seulement si interne
    bool CanAccessFunction(HttpContext context,
        DeploymentInformation function, string path);

    // Filtre les subscribers d’un event (public => toujours, private => seulement si interne)
    List<DeploymentInformation> GetAllowedSubscribers(HttpContext context, string eventName);
}
