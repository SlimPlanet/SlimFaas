using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SlimFaas.Logs;

public static partial class LogText
{
    [GeneratedRegex(@"\x1B(?:\][^\x07\x1B]*(?:\x07|\x1B\\)|\[[0-?]*[ -/]*[@-~]|[@-_])|[\x00-\x08\x0B-\x1F\x7F]")]
    private static partial Regex TerminalControls();

    public static LogReadItem Normalize(string value, bool truncated = false)
    {
        if (truncated && value.Length > 0 && char.IsHighSurrogate(value[^1])) value = value[..^1];
        value = TerminalControls().Replace(value, "");
        var bytes = 0;
        var chars = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > LogLimits.LineBytes) { truncated = true; break; }
            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        value = value[..chars];
        long? timestamp = null;
        var space = value.IndexOf(' ');
        if (space is > 15 and < 40 && DateTimeOffset.TryParse(value.AsSpan(0, space),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        { timestamp = date.ToUnixTimeMilliseconds(); value = value[(space + 1)..]; }
        return new LogReadItem(value, timestamp, truncated);
    }

    public static async IAsyncEnumerable<LogReadItem> ReadLinesAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var chunk = new char[4096];
        var line = new StringBuilder();
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(chunk.AsMemory(), ct)) != 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (chunk[i] == '\n')
                {
                    yield return Normalize(line.ToString(), truncated);
                    line.Clear(); truncated = false;
                }
                else if (line.Length < LogLimits.LineBytes) line.Append(chunk[i]);
                else truncated = true;
            }
        }
        if (line.Length > 0 || truncated) yield return Normalize(line.ToString(), truncated);
    }
}
