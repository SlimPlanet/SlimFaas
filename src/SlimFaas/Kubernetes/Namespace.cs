namespace SlimFaas.Kubernetes;

public class Namespace
{
    public static string GetNamespace()
    {
        string? namespace_ = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace);
        if (!string.IsNullOrEmpty(namespace_))
        {
            return namespace_;
        }
        const string namespaceFilePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

        try
        {
            if (File.Exists(namespaceFilePath))
            {
                string namespaceName = File.ReadAllText(namespaceFilePath).Trim();
                Console.WriteLine($"Namespace actuel : {namespaceName}");
                Environment.SetEnvironmentVariable(EnvironmentVariables.Namespace, namespaceName);
                return namespaceName;
            }
            else
            {
                Console.WriteLine("Fichier de namespace introuvable.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur lors de la lecture du namespace : {ex.Message}");
        }
        return EnvironmentVariables.NamespaceDefault;
    }
}
