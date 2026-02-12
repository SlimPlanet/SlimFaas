using Microsoft.Extensions.Logging;

namespace SlimFaas.Kubernetes;

public class Namespace
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
                logger.LogInformation("Namespace file found: {NamespaceName}", namespaceName);
                return namespaceName;
            }

            logger.LogWarning("Namespace file not found");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading namespace file");
        }
        return defaultNamespace;
    }
}
