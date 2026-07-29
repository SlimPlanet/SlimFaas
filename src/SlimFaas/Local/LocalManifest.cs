using YamlDotNet.Serialization;

namespace SlimFaas.Local;

public sealed class LocalManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "slimfaas-local";
    public LocalClusterManifest Cluster { get; set; } = new();
    public LocalPortRangeManifest ProcessPorts { get; set; } = new();
    public LocalStateManifest State { get; set; } = new();
    public Dictionary<string, LocalFunctionManifest> Functions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, LocalJobManifest> Jobs { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, LocalProcessManifest> Processes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LocalClusterManifest
{
    public int Nodes { get; set; } = 1;
    public int EntrypointPort { get; set; } = 30020;
    public int NodeHttpPortBase { get; set; } = 30021;
    public int RaftPortBase { get; set; } = 3262;
    public string NodeLogLevel { get; set; } = "Error";
}

public sealed class LocalPortRangeManifest
{
    public int From { get; set; } = 5000;
    public int To { get; set; } = 5999;
}

public sealed class LocalStateManifest
{
    public string Mode { get; set; } = "persistent";
    public string? Directory { get; set; }
}

public sealed class LocalFunctionManifest
{
    public List<string> Command { get; set; } = [];
    public string WorkingDirectory { get; set; } = ".";
    public string? DebugUrl { get; set; }
    public Dictionary<string, string> Annotations { get; set; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, string> Environment { get; set; } =
        new(StringComparer.Ordinal);
    public LocalHealthManifest Health { get; set; } = new();
    public LocalShutdownManifest? Shutdown { get; set; }
}

public sealed class LocalHealthManifest
{
    public string Path { get; set; } = "/health";
    public int PeriodSeconds { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 2;
    public int StartupTimeoutSeconds { get; set; } = 120;
}

public sealed class LocalShutdownManifest
{
    public string Path { get; set; } = "/shutdown";
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class LocalJobManifest
{
    public List<string> Command { get; set; } = [];
    public string WorkingDirectory { get; set; } = ".";
    public Dictionary<string, string> Annotations { get; set; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, string> Environment { get; set; } =
        new(StringComparer.Ordinal);
    public int TtlSecondsAfterFinished { get; set; } = 60;
    public int BackoffLimit { get; set; } = 1;
    public string RestartPolicy { get; set; } = "Never";
    public LocalJobResourcesManifest? Resources { get; set; }
}

public sealed class LocalJobResourcesManifest
{
    public Dictionary<string, string> Requests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LocalProcessManifest
{
    public List<string> Command { get; set; } = [];
    public string WorkingDirectory { get; set; } = ".";
    public Dictionary<string, string> Environment { get; set; } =
        new(StringComparer.Ordinal);
    public string? Port { get; set; }
    public string RestartPolicy { get; set; } = "always";
}

[YamlStaticContext]
[YamlSerializable(typeof(LocalManifest))]
[YamlSerializable(typeof(LocalClusterManifest))]
[YamlSerializable(typeof(LocalPortRangeManifest))]
[YamlSerializable(typeof(LocalStateManifest))]
[YamlSerializable(typeof(LocalFunctionManifest))]
[YamlSerializable(typeof(LocalHealthManifest))]
[YamlSerializable(typeof(LocalShutdownManifest))]
[YamlSerializable(typeof(LocalJobManifest))]
[YamlSerializable(typeof(LocalJobResourcesManifest))]
[YamlSerializable(typeof(LocalProcessManifest))]
public partial class LocalManifestYamlContext : StaticContext;

public sealed record LoadedLocalManifest(
    LocalManifest Manifest,
    string ManifestPath,
    string BaseDirectory,
    string StateDirectory,
    bool IsPersistent)
{
    public IReadOnlyList<string> ManifestPaths { get; init; } = [ManifestPath];
}
