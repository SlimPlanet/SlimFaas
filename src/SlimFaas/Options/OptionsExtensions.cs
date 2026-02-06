using Microsoft.Extensions.Options;

namespace SlimFaas.Options;

/// <summary>
/// Extension methods for registering SlimFaas options
/// </summary>
public static class OptionsExtensions
{
    /// <summary>
    /// Adds and configures SlimFaas options from configuration
    /// </summary>
    public static IServiceCollection AddSlimFaasOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SlimFaasOptions>()
            .BindConfiguration(SlimFaasOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SlimDataOptions>()
            .BindConfiguration(SlimDataOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<WorkersOptions>()
            .BindConfiguration(WorkersOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Gets a temporary directory path for SlimData storage
    /// </summary>
    public static string GetTemporaryDirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        if (File.Exists(tempDirectory))
        {
            return GetTemporaryDirectory();
        }

        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }
}
