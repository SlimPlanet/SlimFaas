using System.Text.Json.Serialization;

namespace SlimFaas.Workers;

/// <summary>
/// Represents the backup of all ScheduleJob hashsets.
/// Each key is a hashset key (e.g. "ScheduleJob:default"), and the value
/// is a dictionary of schedule-id → base64-encoded MemoryPack bytes.
/// </summary>
public sealed class ScheduleJobBackupData
{
    public Dictionary<string, Dictionary<string, string>> Hashsets { get; set; } = new();
}

[JsonSerializable(typeof(ScheduleJobBackupData))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class ScheduleJobBackupDataJsonContext : JsonSerializerContext;

