using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlimFaas.Options;

namespace SlimFaas.Kubernetes;

/// <summary>
/// Provides the current Kubernetes namespace from configuration or service account
/// </summary>
public class NamespaceProvider : INamespaceProvider
{
    private readonly string _namespace;

    public NamespaceProvider(IOptions<SlimFaasOptions> options, ILogger<NamespaceProvider> logger)
    {
        var configuredNamespace = options.Value.Namespace;
        _namespace = Namespace.GetNamespace(logger, configuredNamespace);

        if (_namespace != configuredNamespace)
        {
            logger.LogInformation(
                "Namespace resolved from Kubernetes service account: {ResolvedNamespace} (configured: {ConfiguredNamespace})",
                _namespace,
                configuredNamespace);
        }
        else
        {
            logger.LogInformation("Using configured namespace: {Namespace}", _namespace);
        }
    }

    /// <inheritdoc />
    public string CurrentNamespace => _namespace;
}
