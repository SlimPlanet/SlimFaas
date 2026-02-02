﻿namespace SlimFaas.Kubernetes;

public class Namespace
{
    /// <summary>
    /// Gets the namespace from Kubernetes service account or returns default
    /// </summary>
    public static string GetNamespace(string defaultNamespace = "default")
    {
        const string namespaceFilePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

        try
        {
            if (File.Exists(namespaceFilePath))
            {
                string namespaceName = File.ReadAllText(namespaceFilePath).Trim();
                Console.WriteLine($"Namespace actuel : {namespaceName}");
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
        return defaultNamespace;
    }
}
