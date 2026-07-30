using SlimFaas.Kubernetes;

namespace SlimFaas;

internal static class DependencyReference
{
    internal const string LocalProcessPrefix = "processes:";

    internal static bool TryGetLocalProcessName(string dependency, out string processName)
    {
        if (dependency.StartsWith(LocalProcessPrefix, StringComparison.OrdinalIgnoreCase))
        {
            processName = dependency[LocalProcessPrefix.Length..];
            return true;
        }

        processName = "";
        return false;
    }

    internal static bool IsLocalProcessReady(
        DeploymentsInformations deployments,
        string dependency)
    {
        if (!TryGetLocalProcessName(dependency, out string processName))
            return true;

        // A null map identifies Docker and Kubernetes implementations, where
        // local process dependencies deliberately have no effect.
        if (deployments.LocalProcesses is null)
            return true;

        return deployments.LocalProcesses.Any(
            item => item.Key.Equals(processName, StringComparison.OrdinalIgnoreCase) &&
                    item.Value);
    }
}
