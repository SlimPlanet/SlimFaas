using System.Text;
using System.Text.RegularExpressions;
using ZLinq;

namespace SlimFaas.Kubernetes;

// Target identity distinguishes identically labelled samples scraped from different pods.
// Aggregation drops the target, just as it drops labels outside the grouping clause.
internal readonly record struct SeriesKey(
    string? Deployment, string? Pod, KeyValuePair<string, string>[]? Labels)
{
    public bool Equals(SeriesKey other) =>
        Deployment == other.Deployment && Pod == other.Pod && Labels.AsSpan().SequenceEqual(other.Labels);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Deployment);
        hash.Add(Pod);
        foreach (var label in Labels.AsSpan()) hash.Add(label);
        return hash.ToHashCode();
    }

    public string Get(string name)
    {
        foreach (var label in Labels.AsSpan())
            if (label.Key == name) return label.Value;
        return "";
    }

    public SeriesKey Group(string[] names)
    {
        if (names.Length == 0 || Labels is null) return default;
        return new(null, null, Labels.AsValueEnumerable().Where(p => names.Contains(p.Key)).ToArray());
    }

    public SeriesKey WithoutBucket() =>
        new(Deployment, Pod, Labels?.AsValueEnumerable().Where(static p => p.Key != "le").ToArray());
}

internal sealed class MetricSelector(string name)
{
    public string MetricName => name;
    public List<(string Label, string Value, Regex? Pattern, bool Negative)> Matchers { get; } = [];

    public bool TrySelect(string metricKey, string deployment, string pod, out SeriesKey key)
    {
        key = default;
        int start = metricKey.IndexOf('{', StringComparison.Ordinal);
        if (!metricKey.AsSpan(0, start < 0 ? metricKey.Length : start).SequenceEqual(name)) return false;

        List<KeyValuePair<string, string>>? labels = null;
        if (start >= 0)
        {
            if (metricKey[^1] != '}') return false;
            var block = metricKey.AsSpan(start + 1, metricKey.Length - start - 2).Trim();
            while (!block.IsEmpty)
            {
                int equals = block.IndexOf('=');
                if (equals <= 0) return false;
                var label = block[..equals].Trim().ToString();
                block = block[(equals + 1)..].TrimStart();
                if (!TryReadQuoted(block, out var value, out int length)) return false;
                if (value.Length > 0) (labels ??= []).Add(new(label, value));
                block = block[length..].TrimStart();
                if (block.IsEmpty) break;
                if (block[0] != ',') return false;
                block = block[1..].TrimStart();
            }
        }

        labels?.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        key = new(deployment, pod, labels?.ToArray());
        foreach (var matcher in Matchers)
        {
            string actual = matcher.Label == "__name__" ? name : key.Get(matcher.Label);
            bool matches = matcher.Pattern?.IsMatch(actual) ?? matcher.Value == actual;
            if (matches == matcher.Negative) return false;
        }
        return true;
    }

    internal static bool TryReadQuoted(ReadOnlySpan<char> text, out string value, out int length)
    {
        value = "";
        length = 0;
        if (text.IsEmpty || text[0] != '"') return false;
        StringBuilder? decoded = null;
        int segment = 1;
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                value = decoded is null ? text[segment..i].ToString() : decoded.Append(text[segment..i]).ToString();
                length = i + 1;
                return true;
            }
            if (text[i] != '\\') continue;
            (decoded ??= new()).Append(text[segment..i]);
            if (++i >= text.Length) return false;
            switch (text[i])
            {
                case 'n': decoded.Append('\n'); break;
                case 't': decoded.Append('\t'); break;
                case 'r': decoded.Append('\r'); break;
                case '\\': case '"': decoded.Append(text[i]); break;
                default: return false;
            }
            segment = i + 1;
        }
        return false;
    }
}
