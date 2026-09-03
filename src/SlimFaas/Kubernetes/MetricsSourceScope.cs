using System.Globalization;

namespace SlimFaas.Kubernetes;

public static class MetricsSourceScope
{
    public const string LocalSourceName = "pods";

    public static string ForExternal(string function, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"external:{function.Length}:{function}:{source}");
    }

    public static string Label(string? source) =>
        string.IsNullOrWhiteSpace(source) ? LocalSourceName : source;
}
