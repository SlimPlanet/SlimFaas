using SlimFaas.RateLimiting;

namespace SlimFaas.Extensions;

public static class RateLimitingExtensions
{
    public static IServiceCollection AddCpuRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var rateLimitingOptions = GetRateLimitingOptions(configuration);

        if (!rateLimitingOptions.Enabled)
        {
            return services;
        }

        services
            .AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .Validate(options => !options.Enabled || options.IsValid(),
                "Invalid RateLimiting configuration.");

        services.AddSingleton<CpuMetrics>();
        services.AddSingleton<ICpuMetrics>(sp => sp.GetRequiredService<CpuMetrics>());
        services.AddHostedService<CpuMonitoringWorker>();

        return services;
    }

    public static IApplicationBuilder UseCpuRateLimiting(
        this IApplicationBuilder app,
        params int[] excludedPorts)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var rateLimitingOptions = GetRateLimitingOptions(configuration);

        if (rateLimitingOptions.Enabled)
        {
            app.UseMiddleware<CpuRateLimitingMiddleware>(excludedPorts);
        }

        return app;
    }

    private static RateLimitingOptions GetRateLimitingOptions(IConfiguration configuration) =>
        configuration
            .GetSection(RateLimitingOptions.SectionName)
            .Get<RateLimitingOptions>() ?? new RateLimitingOptions();
}
