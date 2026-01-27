using System.Text;
using System.Text.Json.Serialization;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace SlimFaasMcpGateway.Audit;

/// <summary>
/// Text-based diff using DiffPlex for git-like diff output
/// </summary>
public static class TextDiff
{
    public sealed record DiffLine(
        [property: JsonPropertyName("type")] DiffLineType Type,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("position")] int? Position = null
    );

    [JsonConverter(typeof(JsonStringEnumConverter<DiffLineType>))]
    public enum DiffLineType
    {
        Unchanged,
        Inserted,
        Deleted,
        Modified,
        Imaginary
    }

    public sealed record UnifiedDiff(
        [property: JsonPropertyName("lines")] IReadOnlyList<DiffLine> Lines
    );

    /// <summary>
    /// Creates a unified diff between two text strings
    /// </summary>
    public static UnifiedDiff Create(string oldText, string newText)
    {
        var differ = new Differ();
        var builder = new InlineDiffBuilder(differ);
        var diff = builder.BuildDiffModel(oldText, newText);

        var lines = new List<DiffLine>();
        var position = 1;

        foreach (var line in diff.Lines)
        {
            var type = line.Type switch
            {
                ChangeType.Unchanged => DiffLineType.Unchanged,
                ChangeType.Inserted => DiffLineType.Inserted,
                ChangeType.Deleted => DiffLineType.Deleted,
                ChangeType.Modified => DiffLineType.Modified,
                ChangeType.Imaginary => DiffLineType.Imaginary,
                _ => DiffLineType.Unchanged
            };

            // Skip imaginary lines for cleaner output
            if (type == DiffLineType.Imaginary)
                continue;

            lines.Add(new DiffLine(type, line.Text, type != DiffLineType.Deleted ? position : null));

            if (type != DiffLineType.Deleted)
                position++;
        }

        return new UnifiedDiff(lines);
    }

    /// <summary>
    /// Formats a diff as a traditional unified diff string
    /// </summary>
    public static string Format(UnifiedDiff diff, string? fromLabel = null, string? toLabel = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(fromLabel) && !string.IsNullOrEmpty(toLabel))
        {
            sb.AppendLine($"--- {fromLabel}");
            sb.AppendLine($"+++ {toLabel}");
        }

        foreach (var line in diff.Lines)
        {
            var prefix = line.Type switch
            {
                DiffLineType.Inserted => '+',
                DiffLineType.Deleted => '-',
                DiffLineType.Modified => '!',
                _ => ' '
            };

            sb.AppendLine($"{prefix}{line.Text}");
        }

        return sb.ToString();
    }
}
