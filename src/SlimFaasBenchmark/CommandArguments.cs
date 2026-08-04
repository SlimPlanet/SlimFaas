using System.Globalization;

namespace SlimFaasBenchmark;

internal sealed class CommandArguments
{
    private readonly Dictionary<string, string> _values;

    private CommandArguments(Dictionary<string, string> values) => _values = values;

    public static CommandArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
                throw new ArgumentException($"Unexpected argument '{argument}'. Options must use --name value.");

            string key = argument[2..];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option '--{key}' requires a value.");
            values[key] = args[++index];
        }

        return new CommandArguments(values);
    }

    public string Get(string name, string defaultValue) =>
        _values.TryGetValue(name, out string? value) ? value : defaultValue;

    public string? GetOptional(string name) =>
        _values.TryGetValue(name, out string? value) ? value : null;

    public int GetInt(string name, int defaultValue, int minimum, int maximum)
    {
        string raw = Get(name, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
            value < minimum || value > maximum)
        {
            throw new ArgumentException($"--{name} must be between {minimum} and {maximum}.");
        }

        return value;
    }

    public IReadOnlyList<int> GetIntList(
        string name,
        string defaultValue,
        int minimum,
        int maximum)
    {
        string raw = Get(name, defaultValue);
        int[] result = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value =>
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
                    parsed < minimum || parsed > maximum)
                {
                    throw new ArgumentException(
                        $"Every --{name} value must be between {minimum} and {maximum}.");
                }

                return parsed;
            })
            .Distinct()
            .ToArray();

        if (result.Length == 0)
            throw new ArgumentException($"--{name} must contain at least one value.");
        return result;
    }

    public IReadOnlyList<Uri> GetUriList(string name, string defaultValue)
    {
        Uri[] result = Get(name, defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri)
                ? uri
                : throw new ArgumentException($"Invalid URI '{value}' in --{name}."))
            .ToArray();
        if (result.Length == 0)
            throw new ArgumentException($"--{name} must contain at least one URI.");
        return result;
    }
}

internal static class BenchmarkCli
{
    public static void PrintUsage() => Console.WriteLine(
        """
        SlimFaasBenchmark:
          target [--port 31080]
          compare --baseline <baseline-results.json> --candidate <candidate-results.json>
                  [--output artifacts/slimfaas-local-benchmark/comparison]
          run [--slimfaas-url http://127.0.0.1:31020]
              [--direct-url http://127.0.0.1:31080]
              [--node-urls http://127.0.0.1:31021,http://127.0.0.1:31022,http://127.0.0.1:31023]
              [--function benchmark-latency] [--scale-function benchmark-scale]
              [--duration 10] [--warmup 2] [--repetitions 3]
              [--payload-bytes 64,4096,262144,2097152]
              [--concurrency 1,16] [--async-drain-timeout 180]
              [--scale-messages 200] [--scale-concurrency 32]
              [--scale-target-replicas 4] [--scale-timeout 120]
              [--output artifacts/slimfaas-local-benchmark/manual]
        """);
}
