using System.Text.Json;

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
    int NumberParallelRequestPerPod);

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
            ParseJson(
                annotations,
                FunctionAnnotationNames.Configuration,
                SlimFaasConfigurationSerializerContext.Default.SlimFaasConfiguration,
                new SlimFaasConfiguration()),
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
            ParseInt(annotations, FunctionAnnotationNames.NumberParallelRequestPerPod, 10, minimum: 1));
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

    private static IList<string> ParseCsv(IReadOnlyDictionary<string, string> values, string key)
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

    private static IList<T> ParseVisibilityList<T>(
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
            int separator = token.IndexOf(':');
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
