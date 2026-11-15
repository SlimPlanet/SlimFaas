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

    public bool DurableWrites { get; set; }

    public int RequiredDurableCopies { get; set; }

    public int AdmissionTimeoutSeconds { get; set; } = 120;

    public int ReconcileIntervalSeconds { get; set; } = 5;
    public int ReconcileBatchSize { get; set; } = 16;
    public long ReconcileMaxBytes { get; set; } = 32L * 1024L * 1024L;
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
            .Validate(
                options => !options.DurableWrites || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
                "Durable Files directory publication is supported on Linux and macOS.")
            .Validate(options => options.RequiredDurableCopies is >= 0 and <= 2,
                "Required durable Files copies must be 0 (legacy), 1 (local), or 2 (local and remote).")
            .Validate(options => options.RequiredDurableCopies == 0 || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
                "Durable Files admission is supported on Linux and macOS.")
            .Validate(options => options.AdmissionTimeoutSeconds is >= 1 and <= 600,
                "Files admission timeout must be between 1 and 600 seconds.")
            .Validate(options => options.ReconcileIntervalSeconds > 0 && options.ReconcileBatchSize is >= 1 and <= 1024 &&
                                 options.ReconcileMaxBytes > 0,
                "Files reconciliation requires a positive interval/byte budget and a batch size between 1 and 1024.")
            .ValidateOnStart();
}
