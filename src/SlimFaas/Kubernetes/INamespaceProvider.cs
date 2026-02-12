namespace SlimFaas.Kubernetes;

/// <summary>
/// Provides the current Kubernetes namespace
/// </summary>
public interface INamespaceProvider
{
    /// <summary>
    /// Gets the current namespace
    /// </summary>
    string CurrentNamespace { get; }
}
