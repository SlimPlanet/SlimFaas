using System.Text.Json.Serialization;

namespace SlimFaas.Kubernetes;

public class ScheduleConfig
{
    public string TimeZoneID { get; set; } = "GB";
    public DefaultSchedule? Default { get; set; } = new();
}

public record DefaultSchedule
{
    public List<string> WakeUp { get; init; } = new();
    public List<ScaleDownTimeout> ScaleDownTimeout { get; init; } = new();
}

public record ScaleDownTimeout
{
    public string Time { get; init; } = "00:00";
    public int? Value { get; init; }
    public int? MinActiveReplicas { get; init; }
    public int? MinIdleReplicas { get; init; }

    // Read the original annotation names when used inside a schedule.
    public int? ReplicasAtStart { get; init; }
    public int? ReplicasMin { get; init; }
}

[JsonSerializable(typeof(ScheduleConfig))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ScheduleConfigSerializerContext : JsonSerializerContext;
