using Microsoft.Extensions.Logging;

namespace SlimFaas.Kubernetes;

public static class Namespace
{
    /// <summary>
    /// Gets the namespace from Kubernetes service account or returns default
    /// </summary>
    public static string GetNamespace(ILogger logger, string defaultNamespace = "default")
    {
        const string namespaceFilePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

        try
        {
            if (File.Exists(namespaceFilePath))
            {
                string namespaceName = File.ReadAllText(namespaceFilePath).Trim();
                logger.LogNamespaceFileFound(namespaceName);
                return namespaceName;
            }

            logger.LogNamespaceFileNotFound();
        }
        catch (Exception ex)
        {
            logger.LogErrorReadingNamespaceFile(ex);
        }
        return defaultNamespace;
    }
}
