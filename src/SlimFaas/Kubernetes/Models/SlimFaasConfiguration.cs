using System.Text.Json.Serialization;

namespace SlimFaas.Kubernetes;

public record SlimFaasConfiguration
{
    public SlimFaasDefaultConfiguration DefaultSync { get; init; } = new();
    public SlimFaasDefaultConfiguration DefaultAsync { get; init; } = new();
    public SlimFaasDefaultConfiguration DefaultPublish { get; init; } = new();
}

public record SlimFaasDefaultConfiguration
{
    public int HttpTimeout { get; init; } = 120;
    public List<int> TimeoutRetries { get; init; } = [2, 4, 8];
    public List<int> HttpStatusRetries { get; init; } = [500, 502, 503];
}

[JsonSerializable(typeof(SlimFaasConfiguration))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SlimFaasConfigurationSerializerContext : JsonSerializerContext;
