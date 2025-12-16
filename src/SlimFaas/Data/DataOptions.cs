using SlimFaas.Kubernetes;

namespace SlimFaas.Options;

public sealed class DataOptions
{
    public const string SectionName = "Data";

    // "Public" | "Private" dans appsettings.json
    public FunctionVisibility DefaultVisibility { get; set; } = FunctionVisibility.Private;
}
