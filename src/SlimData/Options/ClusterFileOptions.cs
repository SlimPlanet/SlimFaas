using Microsoft.Extensions.Options;

namespace SlimData.Options;

public sealed class ClusterFileOptions
{
    public const string SectionName = "SlimData:Files";

    public long MaxInFlightBytes { get; set; } = 256L * 1024L * 1024L;

    public long UnknownLengthReservationBytes { get; set; } = 256L * 1024L * 1024L;

    public int MaxPendingTransfers { get; set; } = 128;

    public int QueueWaitTimeoutSeconds { get; set; } = 30;

    public bool DropPageCache { get; set; } = true;
}

public static class ClusterFileOptionsExtensions
{
    public static OptionsBuilder<ClusterFileOptions> AddClusterFileOptions(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services.AddOptions<ClusterFileOptions>()
            .Bind(configuration.GetSection(ClusterFileOptions.SectionName))
            .Validate(
                options => options.MaxInFlightBytes > 0,
                "SlimData file maximum in-flight bytes must be positive.")
            .Validate(
                options => options.UnknownLengthReservationBytes > 0,
                "SlimData file unknown-length reservation must be positive.")
            .Validate(
                options => options.MaxPendingTransfers > 0,
                "SlimData file maximum pending transfers must be positive.")
            .Validate(
                options => options.QueueWaitTimeoutSeconds > 0,
                "SlimData file queue wait timeout must be positive.")
            .ValidateOnStart();
}
