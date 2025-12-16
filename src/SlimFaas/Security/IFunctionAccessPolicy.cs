using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Security;

public interface IFunctionAccessPolicy
{
    // True = appel venant d’un pod “trusted” / job (donc interne)
    bool IsInternalRequest(HttpContext context, IReplicasService replicasService, IJobService jobService);

    // Résout la visibilité effective selon le path (PathsStartWithVisibility) sinon function.Visibility
    FunctionVisibility ResolveVisibility(DeploymentInformation function, string path);

    // Décision finale : public => OK, private => OK seulement si interne
    bool CanAccessFunction(HttpContext context, IReplicasService replicasService, IJobService jobService,
        DeploymentInformation function, string path);

    // Filtre les subscribers d’un event (public => toujours, private => seulement si interne)
    List<DeploymentInformation> GetAllowedSubscribers(HttpContext context, IReplicasService replicasService,
        IJobService jobService, string eventName);
}
