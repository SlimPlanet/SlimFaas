using SlimFaas.Kubernetes;


public class JobResourceValidator
{
    public static CreateJobResources ValidateResources(CreateJobResources? defaultConfig, CreateJobResources? requestedConfig)
    {
        if (defaultConfig == null)
        {
            throw new ArgumentException("Default configuration must be provided");
        }

        if (requestedConfig == null)
        {
            return defaultConfig;
        }

        return new CreateJobResources(
            ValidateResourceValues(defaultConfig.Requests, requestedConfig.Requests),
            ValidateResourceValues(defaultConfig.Limits, requestedConfig.Limits)
        );
    }

    private static Dictionary<string, string> ValidateResourceValues(Dictionary<string, string> defaultValues, Dictionary<string, string> requestedValues)
    {
        var validatedResources = new Dictionary<string, string>();

        foreach (var (key, defaultValue) in defaultValues)
        {
            if (requestedValues.TryGetValue(key, out var requestedValue) && !ExceedsLimit(defaultValue, requestedValue))
            {
                validatedResources[key] = requestedValue;
            }
            else
            {
                validatedResources[key] = defaultValue;
            }
        }

        return validatedResources;
    }

    private static bool ExceedsLimit(string maxValue, string requestedValue)
    {
        return ParseResourceValue(requestedValue) > ParseResourceValue(maxValue);
    }

    private static double ParseResourceValue(string value)
    {
        if (value.EndsWith("m"))
        {
            return double.Parse(value.TrimEnd('m')) / 1000.0; // Convert milliCPU to CPU
        }
        else if (value.EndsWith("Mi"))
        {
            return double.Parse(value.TrimEnd('M', 'i')); // Memory in MiB
        }
        else if (value.EndsWith("Gi"))
        {
            return double.Parse(value.TrimEnd('G', 'i')) * 1024; // Convert GiB to MiB
        }
        else
        {
            return double.Parse(value); // Assume plain values are CPU or raw MiB
        }
    }
}
