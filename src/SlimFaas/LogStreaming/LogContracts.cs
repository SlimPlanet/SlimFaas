using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace SlimFaas.Logs;

public record LogTarget(string Kind, string Name, string? Replica = null);
public record LogSourceKey(LogTarget Target, string Instance, string Container, string Generation);
public record LogSource(string Id, string Name, string Container);
public record LogSources(string Status, IReadOnlyList<LogSource> Sources);
// Status is used only on the authenticated supervisor channel, never as log text.
public record LogReadItem(string Text, long? TimestampMs = null, bool Truncated = false, string? Status = null);
public record LogLine(long Id, string Text, long? TimestampMs, bool Truncated);
public record LogState(string Status, string Session, long DroppedLines, int MaxLines = LogLimits.Lines,
    int MaxBytes = LogLimits.Bytes);
public record LogBatch(IReadOnlyList<LogLine> Lines);

public static class LogLimits
{
    public const int Lines = 10_000;
    public const int Bytes = 8 * 1024 * 1024;
    public const int LineBytes = 16 * 1024;
    public const int Sources = 4;
}

public interface IInstanceLogProvider
{
    Task<LogSources> GetSourcesAsync(LogTarget target, CancellationToken ct);
    IAsyncEnumerable<LogReadItem> ReadAsync(string source, CancellationToken ct);
}

public static class LogSourceIds
{
    // A portable resource reference, not an authorization token. Every read must
    // revalidate it against the orchestrator's managed resources and generation.
    public static LogSource Create(LogSourceKey key) => new(
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(key, LogJsonContext.Default.LogSourceKey)),
        key.Instance, key.Container);

    public static LogSourceKey Parse(string source)
    {
        if (source.Length is 0 or > 4096) throw new ArgumentException("Invalid log source");
        try
        {
            var key = JsonSerializer.Deserialize(WebEncoders.Base64UrlDecode(source), LogJsonContext.Default.LogSourceKey);
            if (key is null || !ValidTarget(key.Target) || string.IsNullOrEmpty(key.Instance) ||
                key.Instance.Length > 256 || key.Container is null || key.Container.Length > 256 ||
                key.Generation is null || key.Generation.Length > 256) throw new ArgumentException("Invalid log source");
            return key;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new ArgumentException("Invalid log source", ex); }
    }

    public static bool ValidTarget(LogTarget? target) => target is not null &&
        target.Kind is "function" or "job" or "slimfaas" &&
        !string.IsNullOrWhiteSpace(target.Name) && target.Name.Length <= 256 &&
        (!string.IsNullOrWhiteSpace(target.Replica) && target.Replica.Length <= 256);
}

[JsonSerializable(typeof(LogSourceKey))]
[JsonSerializable(typeof(LogSources))]
[JsonSerializable(typeof(LogReadItem))]
[JsonSerializable(typeof(LogState))]
[JsonSerializable(typeof(LogBatch))]
public partial class LogJsonContext : JsonSerializerContext;
