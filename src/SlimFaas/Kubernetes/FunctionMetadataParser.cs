using System.Text.Json;
using System.Globalization;

namespace SlimFaas.Kubernetes;

public static class FunctionAnnotationNames
{
    public const string Function = "SlimFaas/Function";
    public const string ReplicasMin = "SlimFaas/ReplicasMin";
    public const string ReplicasAtStart = "SlimFaas/ReplicasAtStart";
    public const string TimeoutSecondBeforeSetReplicasMin = "SlimFaas/TimeoutSecondBeforeSetReplicasMin";
    public const string NumberParallelRequest = "SlimFaas/NumberParallelRequest";
    public const string NumberParallelRequestPerPod = "SlimFaas/NumberParallelRequestPerPod";
    public const string ReplicasStartAsSoonAsOneFunctionRetrieveARequest =
        "SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest";
    public const string DependsOn = "SlimFaas/DependsOn";
    public const string SubscribeEvents = "SlimFaas/SubscribeEvents";
    public const string SubscribeStream = "SlimFaas/SubscribeStream";
    public const string StreamMaxAge = "SlimFaas/StreamMaxAge";
    public const string StreamMaxLength = "SlimFaas/StreamMaxLength";
    public const string DefaultVisibility = "SlimFaas/DefaultVisibility";
    public const string PathsStartWithVisibility = "SlimFaas/PathsStartWithVisibility";
    public const string DefaultTrust = "SlimFaas/DefaultTrust";
    public const string Configuration = "SlimFaas/Configuration";
    public const string Schedule = "SlimFaas/Schedule";
    public const string Scale = "SlimFaas/Scale";
}

public sealed record FunctionMetadata(
    SlimFaasConfiguration Configuration,
    int ReplicasAtStart,
    int ReplicasMin,
    int TimeoutSecondBeforeSetReplicasMin,
    int NumberParallelRequest,
    bool ReplicasStartAsSoonAsOneFunctionRetrieveARequest,
    IList<string> DependsOn,
    ScheduleConfig? Schedule,
    IList<SubscribeEvent> SubscribeEvents,
    FunctionVisibility Visibility,
    IList<PathVisibility> PathsStartWithVisibility,
    FunctionTrust Trust,
    ScaleConfig? Scale,
    int NumberParallelRequestPerPod,
    IList<SubscribeStream>? SubscribeStreams = null,
    int StreamMaxAgeSeconds = 86400,
    int StreamMaxLength = 100000);

public sealed record SubscribeStream(string Name, FunctionVisibility Visibility);

public static class FunctionMetadataParser
{
    public static bool IsFunction(IReadOnlyDictionary<string, string> annotations)
        => annotations.TryGetValue(FunctionAnnotationNames.Function, out string? value) &&
           bool.TryParse(value, out bool enabled) &&
           enabled;

    public static FunctionMetadata Parse(IReadOnlyDictionary<string, string> annotations, string functionName)
    {
        FunctionVisibility visibility = ParseEnum(
            annotations,
            FunctionAnnotationNames.DefaultVisibility,
            FunctionVisibility.Public);

        return new FunctionMetadata(
            ParseConfiguration(annotations),
            ParseInt(annotations, FunctionAnnotationNames.ReplicasAtStart, 1, minimum: 0),
            ParseInt(annotations, FunctionAnnotationNames.ReplicasMin, 0, minimum: 0),
            ParseInt(annotations, FunctionAnnotationNames.TimeoutSecondBeforeSetReplicasMin, 300, minimum: 0),
            ParseInt(annotations, FunctionAnnotationNames.NumberParallelRequest, 10, minimum: 1),
            ParseBool(annotations, FunctionAnnotationNames.ReplicasStartAsSoonAsOneFunctionRetrieveARequest, false),
            ParseCsv(annotations, FunctionAnnotationNames.DependsOn),
            ParseOptionalJson(
                annotations,
                FunctionAnnotationNames.Schedule,
                ScheduleConfigSerializerContext.Default.ScheduleConfig) ?? new ScheduleConfig(),
            ParseVisibilityList<SubscribeEvent>(
                annotations,
                FunctionAnnotationNames.SubscribeEvents,
                visibility,
                static (value, itemVisibility) => new SubscribeEvent(value, itemVisibility)),
            visibility,
            ParseVisibilityList<PathVisibility>(
                annotations,
                FunctionAnnotationNames.PathsStartWithVisibility,
                FunctionVisibility.Public,
                static (value, itemVisibility) => new PathVisibility(value, itemVisibility)),
            ParseEnum(annotations, FunctionAnnotationNames.DefaultTrust, FunctionTrust.Trusted),
            ParseScale(annotations),
            ParseInt(annotations, FunctionAnnotationNames.NumberParallelRequestPerPod, 10, minimum: 1),
            ParseStreams(annotations, visibility),
            ParseStreamMaxAge(annotations),
            ParseInt(annotations, FunctionAnnotationNames.StreamMaxLength, 100000, minimum: 1));
    }

    private static List<SubscribeStream> ParseStreams(
        IReadOnlyDictionary<string, string> annotations, FunctionVisibility defaultVisibility)
    {
        var result = new List<SubscribeStream>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in ParseCsv(annotations, FunctionAnnotationNames.SubscribeStream))
        {
            string name = token;
            var visibility = defaultVisibility;
            int separator = token.IndexOf(':', StringComparison.Ordinal);
            if (separator >= 0)
            {
                if (!Enum.TryParse(token[..separator], true, out visibility) || !Enum.IsDefined(visibility))
                    throw new FormatException("SlimFaas/SubscribeStream has an invalid visibility.");
                name = token[(separator + 1)..].Trim();
            }
            if (!IdValidator.IsSafeId(name) || !names.Add(name))
                throw new FormatException("SlimFaas/SubscribeStream requires unique, safe Stream names.");
            result.Add(new(name, visibility));
        }
        return result;
    }

    private static int ParseStreamMaxAge(IReadOnlyDictionary<string, string> annotations)
    {
        if (!annotations.TryGetValue(FunctionAnnotationNames.StreamMaxAge, out string? raw))
            return 86400;
        raw = raw.Trim();
        if (raw.Length == 0)
            throw new FormatException("SlimFaas/StreamMaxAge must be a positive duration.");
        int multiplier = raw[^1] switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, _ => 1 };
        string number = char.IsAsciiLetter(raw[^1]) ? raw[..^1] : raw;
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ||
            value <= 0 || value > int.MaxValue / multiplier ||
            (char.IsAsciiLetter(raw[^1]) && raw[^1] is not ('s' or 'm' or 'h' or 'd')))
            throw new FormatException("SlimFaas/StreamMaxAge must be positive seconds or a duration such as 24h.");
        return (int)value * multiplier;
    }

    private static SlimFaasConfiguration ParseConfiguration(IReadOnlyDictionary<string, string> annotations)
    {
        SlimFaasConfiguration configuration = ParseJson(
            annotations,
            FunctionAnnotationNames.Configuration,
            SlimFaasConfigurationSerializerContext.Default.SlimFaasConfiguration,
            new SlimFaasConfiguration());

        return configuration with
        {
            DefaultSync = configuration.DefaultSync ?? new SlimFaasSyncConfiguration(),
            DefaultAsync = NormalizeRetryConfiguration(configuration.DefaultAsync),
            DefaultPublish = NormalizeRetryConfiguration(configuration.DefaultPublish)
        };
    }

    private static SlimFaasDefaultConfiguration NormalizeRetryConfiguration(
        SlimFaasDefaultConfiguration? configuration)
    {
        configuration ??= new SlimFaasDefaultConfiguration();
        DeadLetterQueueConfiguration deadLetters = configuration.DeadLetterQueue ?? new();
        if (deadLetters.MaxAgeSeconds <= 0 || deadLetters.MaxLength <= 0)
            throw new FormatException("DeadLetterQueue.MaxAgeSeconds and MaxLength must be positive integers.");
        return configuration with
        {
            DeadLetterQueue = deadLetters,
            TimeoutRetries = configuration.TimeoutRetries ?? [2, 4, 8],
            HttpStatusRetries = configuration.HttpStatusRetries ?? [500, 502, 503]
        };
    }

    public static ScaleConfig? ParseScale(IReadOnlyDictionary<string, string> annotations)
    {
        ScaleConfig? scale = ParseOptionalJson(
            annotations,
            FunctionAnnotationNames.Scale,
            ScaleConfigSerializerContext.Default.ScaleConfig);
        if (scale is null)
            return null;

        return NormalizeScaleConfig(scale);
    }

    public static ScaleConfig NormalizeScaleConfig(ScaleConfig? scale)
    {
        scale ??= new ScaleConfig();
        if (scale.ScrapeIntervalMilliseconds is { } scrapeInterval &&
            scrapeInterval is < ScaleConfig.MinimumScrapeIntervalMilliseconds or
                > ScaleConfig.MaximumScrapeIntervalMilliseconds)
        {
            throw new FormatException(
                $"{FunctionAnnotationNames.Scale}.ScrapeIntervalMilliseconds must be between " +
                $"{ScaleConfig.MinimumScrapeIntervalMilliseconds} and " +
                $"{ScaleConfig.MaximumScrapeIntervalMilliseconds}.");
        }

        ScaleBehavior behavior = scale.Behavior ?? new ScaleBehavior();
        ScaleDirectionBehavior scaleUp = behavior.ScaleUp ?? ScaleDirectionBehavior.DefaultScaleUp();
        ScaleDirectionBehavior scaleDown = behavior.ScaleDown ?? ScaleDirectionBehavior.DefaultScaleDown();
        IList<ScalePolicy> upPolicies = scaleUp.Policies is { Count: > 0 }
            ? scaleUp.Policies
            : ScaleDirectionBehavior.DefaultScaleUp().Policies;
        IList<ScalePolicy> downPolicies = scaleDown.Policies is { Count: > 0 }
            ? scaleDown.Policies
            : ScaleDirectionBehavior.DefaultScaleDown().Policies;

        return scale with
        {
            Triggers = scale.Triggers ?? [],
            Sources = scale.Sources ?? [],
            Behavior = behavior with
            {
                ScaleUp = scaleUp with { Policies = upPolicies },
                ScaleDown = scaleDown with { Policies = downPolicies }
            }
        };
    }

    private static int ParseInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultValue,
        int minimum)
    {
        if (!values.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!int.TryParse(raw, out int result) || result < minimum)
            throw new FormatException($"{key} must be an integer greater than or equal to {minimum}.");
        return result;
    }

    private static bool ParseBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool defaultValue)
    {
        if (!values.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!bool.TryParse(raw, out bool result))
            throw new FormatException($"{key} must be true or false.");
        return result;
    }

    private static TEnum ParseEnum<TEnum>(
        IReadOnlyDictionary<string, string> values,
        string key,
        TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (!values.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!Enum.TryParse(raw, true, out TEnum result))
            throw new FormatException($"{key} has unsupported value '{raw}'.");
        return result;
    }

    private static string[] ParseCsv(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    private static T ParseJson<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        T defaultValue)
    {
        T? result = ParseOptionalJson(values, key, typeInfo);
        return result is null ? defaultValue : result;
    }

    private static T? ParseOptionalJson<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (!values.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return default;
        string json = JsonMinifier.MinifyJson(raw) ??
                      throw new FormatException($"{key} is not valid JSON.");
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{key} is not valid JSON: {exception.Message}", exception);
        }
    }

    private static List<T> ParseVisibilityList<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        FunctionVisibility defaultVisibility,
        Func<string, FunctionVisibility, T> factory)
    {
        if (!values.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return [];

        var result = new List<T>();
        foreach (string token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string value = token;
            FunctionVisibility visibility = defaultVisibility;
            int separator = token.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                string prefix = token[..separator];
                if (Enum.TryParse(prefix, true, out FunctionVisibility parsed))
                    visibility = parsed;
                value = token[(separator + 1)..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(value))
                result.Add(factory(value, visibility));
        }

        return result;
    }
}
