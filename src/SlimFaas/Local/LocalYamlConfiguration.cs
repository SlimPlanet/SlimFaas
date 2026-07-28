using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SlimFaas.Local;

/// <summary>
/// Loads the untyped YAML documents used by local mode. Keeping this stage
/// separate from the static deserializer lets partial overlays preserve the
/// difference between an omitted value and a value explicitly set to null.
/// </summary>
internal static partial class LocalYamlConfiguration
{
    public static MergedLocalYaml Load(
        IReadOnlyList<string> paths,
        IReadOnlyList<string> environmentFilePaths)
    {
        if (paths.Count == 0)
            throw new LocalManifestException("At least one local manifest must be provided.");

        string[] manifestPaths = paths.Select(Path.GetFullPath).ToArray();
        var effective = new YamlMappingNode();
        foreach (string path in manifestPaths)
        {
            YamlMappingNode overlay = LoadDocument(path);
            MergeMappings(effective, overlay, []);
        }

        IReadOnlyDictionary<string, string> environmentFileValues =
            LoadEnvironmentFiles(environmentFilePaths);
        var interpolatedValues = new HashSet<string>(StringComparer.Ordinal);
        InterpolateValues(effective, environmentFileValues, interpolatedValues);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(effective)).Save(writer, assignAnchors: true);
        return new MergedLocalYaml(manifestPaths, writer.ToString(), interpolatedValues);
    }

    private static YamlMappingNode LoadDocument(string path)
    {
        if (!File.Exists(path))
            throw new LocalManifestException($"Local manifest '{path}' does not exist.");

        var stream = new YamlStream();
        try
        {
            using var reader = File.OpenText(path);
            stream.Load(reader);
        }
        catch (YamlException exception)
        {
            throw InvalidYaml(path, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalManifestException($"Unable to read local manifest '{path}'.", exception);
        }

        if (stream.Documents.Count != 1)
        {
            throw new LocalManifestException(
                $"Local manifest '{path}' must contain exactly one YAML document.");
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new LocalManifestException(
                $"Local manifest '{path}' must contain a YAML mapping at its root.");
        }

        ValidateMappingKeys(root, [], path);
        return root;
    }

    private static IReadOnlyDictionary<string, string> LoadEnvironmentFiles(
        IReadOnlyList<string> environmentFilePaths)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string inputPath in environmentFilePaths)
        {
            string path = Path.GetFullPath(inputPath);
            if (!File.Exists(path))
                throw new LocalManifestException($"Environment file '{path}' does not exist.");

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new LocalManifestException($"Unable to read environment file '{path}'.", exception);
            }

            for (var index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                    trimmed = trimmed["export ".Length..].TrimStart();

                int separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    throw InvalidEnvironmentLine(path, index);

                string name = trimmed[..separator].Trim();
                if (!IsEnvironmentVariableName(name))
                    throw InvalidEnvironmentLine(path, index);

                values[name] = ParseEnvironmentValue(trimmed[(separator + 1)..], path, index);
            }
        }

        return values;
    }

    private static string ParseEnvironmentValue(string input, string path, int lineIndex)
    {
        string value = input.TrimStart();
        if (value.Length == 0)
            return "";

        if (value[0] == '\'')
        {
            int closing = value.IndexOf('\'', 1);
            if (closing < 0 || !IsEmptyOrComment(value[(closing + 1)..]))
                throw InvalidEnvironmentLine(path, lineIndex);
            return value[1..closing];
        }

        if (value[0] == '"')
        {
            var result = new StringBuilder();
            bool escaped = false;
            for (var index = 1; index < value.Length; index++)
            {
                char character = value[index];
                if (escaped)
                {
                    result.Append(character switch
                    {
                        '\\' => '\\',
                        '"' => '"',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => character
                    });
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character != '"')
                {
                    result.Append(character);
                    continue;
                }

                if (!IsEmptyOrComment(value[(index + 1)..]))
                    throw InvalidEnvironmentLine(path, lineIndex);
                return result.ToString();
            }

            throw InvalidEnvironmentLine(path, lineIndex);
        }

        int commentIndex = FindUnquotedComment(value);
        return (commentIndex >= 0 ? value[..commentIndex] : value).TrimEnd();
    }

    private static int FindUnquotedComment(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
                return index;
        }

        return -1;
    }

    private static bool IsEmptyOrComment(string remainder)
    {
        string trimmed = remainder.TrimStart();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    private static bool IsEnvironmentVariableName(string name)
    {
        if (name.Length == 0 || (name[0] != '_' && !char.IsAsciiLetter(name[0])))
            return false;

        return name[1..].All(character =>
            character == '_' || char.IsAsciiLetterOrDigit(character));
    }

    private static LocalManifestException InvalidEnvironmentLine(string path, int lineIndex)
        => new($"Environment file '{path}' contains an invalid entry at line {lineIndex + 1}.");

    private static void ValidateMappingKeys(
        YamlNode node,
        IReadOnlyList<string> path,
        string sourcePath)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                StringComparer comparer = KeyComparer(path);
                var keys = new HashSet<string>(comparer);
                foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: not null } key)
                    {
                        throw new LocalManifestException(
                            $"Local manifest '{sourcePath}' contains a non-scalar mapping key.");
                    }

                    if (!keys.Add(key.Value))
                    {
                        throw new LocalManifestException(
                            $"Local manifest '{sourcePath}' contains duplicate key '{key.Value}' " +
                            $"at line {key.Start.Line + 1}.");
                    }

                    ValidateMappingKeys(valueNode, Append(path, key.Value), sourcePath);
                }

                break;
            }
            case YamlSequenceNode sequence:
                foreach (YamlNode child in sequence.Children)
                    ValidateMappingKeys(child, Append(path, "[]"), sourcePath);
                break;
        }
    }

    private static void MergeMappings(
        YamlMappingNode destination,
        YamlMappingNode overlay,
        IReadOnlyList<string> path)
    {
        foreach ((YamlNode overlayKeyNode, YamlNode overlayValue) in overlay.Children)
        {
            var overlayKey = (YamlScalarNode)overlayKeyNode;
            KeyValuePair<YamlNode, YamlNode>? existing = Find(destination, overlayKey.Value!, path);
            if (IsNull(overlayValue))
            {
                if (existing.HasValue)
                    destination.Children.Remove(existing.Value.Key);
                continue;
            }

            if (existing.HasValue &&
                existing.Value.Value is YamlMappingNode destinationMapping &&
                overlayValue is YamlMappingNode overlayMapping)
            {
                MergeMappings(destinationMapping, overlayMapping, Append(path, overlayKey.Value!));
                continue;
            }

            if (existing.HasValue)
                destination.Children[existing.Value.Key] = overlayValue;
            else
                destination.Add(overlayKeyNode, overlayValue);
        }
    }

    private static KeyValuePair<YamlNode, YamlNode>? Find(
        YamlMappingNode mapping,
        string key,
        IReadOnlyList<string> path)
    {
        StringComparer comparer = KeyComparer(path);
        foreach (KeyValuePair<YamlNode, YamlNode> item in mapping.Children)
        {
            if (item.Key is YamlScalarNode scalar && comparer.Equals(scalar.Value, key))
                return item;
        }

        return null;
    }

    private static StringComparer KeyComparer(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
            return StringComparer.OrdinalIgnoreCase;

        string parent = path[^1];
        if (parent.Equals("annotations", StringComparison.OrdinalIgnoreCase) ||
            parent.Equals("environment", StringComparison.OrdinalIgnoreCase))
        {
            return StringComparer.Ordinal;
        }

        return StringComparer.OrdinalIgnoreCase;
    }

    private static bool IsNull(YamlNode node)
    {
        if (node is not YamlScalarNode scalar)
            return false;

        if (scalar.Tag.ToString().EndsWith(":null", StringComparison.Ordinal))
            return true;

        return scalar.Style is ScalarStyle.Any or ScalarStyle.Plain &&
               (string.IsNullOrEmpty(scalar.Value) ||
                scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                scalar.Value.Equals("~", StringComparison.Ordinal));
    }

    private static void InterpolateValues(
        YamlNode node,
        IReadOnlyDictionary<string, string> environmentFileValues,
        ISet<string> interpolatedValues)
    {
        switch (node)
        {
            case YamlScalarNode scalar when scalar.Value is not null:
            {
                bool replaced = false;
                string expanded = EnvironmentVariablePattern().Replace(scalar.Value, match =>
                {
                    string variableName = match.Groups[1].Value;
                    string? value = Environment.GetEnvironmentVariable(variableName);
                    if (value is null && !environmentFileValues.TryGetValue(variableName, out value))
                    {
                        throw new LocalManifestException(
                            $"Environment variable '{variableName}' referenced by the effective " +
                            "local configuration is not defined.");
                    }

                    replaced = true;
                    interpolatedValues.Add(value);
                    return value;
                });
                if (replaced)
                {
                    scalar.Value = expanded;
                    scalar.Style = ScalarStyle.DoubleQuoted;
                }

                break;
            }
            case YamlMappingNode mapping:
                foreach (YamlNode value in mapping.Children.Values)
                    InterpolateValues(value, environmentFileValues, interpolatedValues);
                break;
            case YamlSequenceNode sequence:
                foreach (YamlNode child in sequence.Children)
                    InterpolateValues(child, environmentFileValues, interpolatedValues);
                break;
        }
    }

    private static IReadOnlyList<string> Append(IReadOnlyList<string> path, string value)
    {
        var result = new string[path.Count + 1];
        for (var index = 0; index < path.Count; index++)
            result[index] = path[index];
        result[^1] = value;
        return result;
    }

    private static LocalManifestException InvalidYaml(string path, YamlException exception)
        => new(
            $"Unable to parse local manifest '{path}' at line {exception.Start.Line + 1}, " +
            $"column {exception.Start.Column + 1}: invalid YAML.",
            exception);

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariablePattern();
}

internal sealed record MergedLocalYaml(
    IReadOnlyList<string> ManifestPaths,
    string Yaml,
    IReadOnlySet<string> InterpolatedValues)
{
    public string Sanitize(string value)
    {
        string result = value;
        foreach (string secret in InterpolatedValues.OrderByDescending(item => item.Length))
        {
            if (secret.Length > 0)
                result = result.Replace(secret, "***", StringComparison.Ordinal);
        }

        return result;
    }
}
