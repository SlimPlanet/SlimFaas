
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
            .Validate(ValidateStatusStreamOptions,
                "SlimFaas:StatusStream values are invalid.")
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

    private static bool ValidateStatusStreamOptions(SlimFaasOptions options)
    {
        var s = options.StatusStream;
        return s.StateIntervalMilliseconds > 0
               && s.QueueLengthsCacheMilliseconds >= 0
               && s.JobsCacheMilliseconds >= 0
               && s.PeerSyncIntervalMilliseconds > 0
               && s.PeerSyncInitialDelayMilliseconds >= 0
               && s.MaxSseClients >= 0
               && s.SubscriberChannelCapacity > 0
               && s.RecentActivityLimit > 0
               && s.KnownIdsLimit > 0
               && s.MaxLiveEventsPerSecond >= 0
               && s.LiveEventSamplingRatio >= 0
               && s.LiveEventSamplingRatio <= 1;
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
