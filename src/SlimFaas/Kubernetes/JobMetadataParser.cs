using System.Text.Json;

namespace SlimFaas.Kubernetes;

public static class JobAnnotationNames
{
    public const string Job = "SlimFaas/Job";
    public const string JobImagesWhitelist = "SlimFaas/JobImagesWhitelist";
    public const string DefaultVisibility = "SlimFaas/DefaultVisibility";
    public const string NumberParallelJob = "SlimFaas/NumberParallelJob";
    public const string DependsOn = "SlimFaas/DependsOn";
    public const string Schedules = "SlimFaas/Schedules";
}

public sealed record JobMetadata(
    FunctionVisibility Visibility,
    int NumberParallelJob,
    List<string> DependsOn,
    IList<ScheduleCreateJob> Schedules);

public static class JobMetadataParser
{
    public static bool IsJob(IReadOnlyDictionary<string, string> annotations)
        => annotations.TryGetValue(JobAnnotationNames.Job, out string? value) &&
           bool.TryParse(value, out bool enabled) &&
           enabled;

    public static JobMetadata Parse(IReadOnlyDictionary<string, string> annotations)
        => new(
            ParseVisibility(annotations),
            ParseNumberParallelJob(annotations),
            ParseCsv(annotations, JobAnnotationNames.DependsOn),
            ParseSchedules(annotations));

    private static FunctionVisibility ParseVisibility(IReadOnlyDictionary<string, string> annotations)
    {
        if (!annotations.TryGetValue(JobAnnotationNames.DefaultVisibility, out string? raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return FunctionVisibility.Private;
        }

        if (!Enum.TryParse(raw, true, out FunctionVisibility visibility) ||
            !Enum.IsDefined(visibility))
        {
            throw new FormatException(
                $"{JobAnnotationNames.DefaultVisibility} must be Public or Private.");
        }

        return visibility;
    }

    private static int ParseNumberParallelJob(IReadOnlyDictionary<string, string> annotations)
    {
        if (!annotations.TryGetValue(JobAnnotationNames.NumberParallelJob, out string? raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return 1;
        }

        if (!int.TryParse(raw, out int result) || result < 1)
        {
            throw new FormatException(
                $"{JobAnnotationNames.NumberParallelJob} must be an integer greater than or equal to 1.");
        }

        return result;
    }

    private static List<string> ParseCsv(IReadOnlyDictionary<string, string> annotations, string key)
        => annotations.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];

    private static IList<ScheduleCreateJob> ParseSchedules(
        IReadOnlyDictionary<string, string> annotations)
    {
        if (!annotations.TryGetValue(JobAnnotationNames.Schedules, out string? raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        string json = JsonMinifier.MinifyJson(raw) ??
                      throw new FormatException($"{JobAnnotationNames.Schedules} is not valid JSON.");
        try
        {
            return JsonSerializer.Deserialize(
                       json,
                       ScheduleCreateJobListSerializerContext.Default.ListScheduleCreateJob) ?? [];
        }
        catch (JsonException exception)
        {
            throw new FormatException(
                $"{JobAnnotationNames.Schedules} is not valid JSON: {exception.Message}",
                exception);
        }
    }
}
