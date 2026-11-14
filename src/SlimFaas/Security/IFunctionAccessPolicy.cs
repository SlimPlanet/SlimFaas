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

    // Visibilité appliquée à un chemin (SlimFaas/PathsStartWithVisibility puis SlimFaas/DefaultVisibility)
    FunctionVisibility ResolveVisibility(DeploymentInformation function, string path);

    // SlimFaas/AllowedCallers : absent => tout appelant interne, sinon l'appelant doit figurer dans la liste
    bool IsAllowedCaller(HttpContext context, DeploymentInformation function);

    // Filtre les subscribers d’un event (public => toujours, private => seulement si interne et autorisé)
    List<DeploymentInformation> GetAllowedSubscribers(HttpContext context, string eventName);
}
