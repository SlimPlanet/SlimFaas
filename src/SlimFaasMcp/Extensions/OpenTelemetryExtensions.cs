using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SlimFaasMcp.Configuration;

namespace SlimFaasMcp.Extensions;

public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddOpenTelemetry(
        this IServiceCollection services,
        OpenTelemetryConfig config)
    {
        if (config.Endpoint == null || string.IsNullOrWhiteSpace(config.Endpoint))
        {
            return services;
        }

        const string serviceName = "SlimFaasMcp";
        string openTelemetryServiceName = string.IsNullOrWhiteSpace(config.ServiceName)
            ? serviceName
            : config.ServiceName;

        services.AddOpenTelemetry()
            .WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(openTelemetryServiceName)
                    .SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService(openTelemetryServiceName)
                        .AddTelemetrySdk())
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(config.Endpoint);
                    });

                if (config.EnableConsoleExporter)
                {
                    tracerProviderBuilder.AddConsoleExporter();
                }

            })
            .WithMetrics(meterProviderBuilder =>
            {
                meterProviderBuilder
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(openTelemetryServiceName)
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(config.Endpoint);
                    });

                if (config.EnableConsoleExporter)
                {
                    meterProviderBuilder.AddConsoleExporter();
                }
            })
            .WithLogging(loggerProviderBuilder =>
            {
                loggerProviderBuilder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService(openTelemetryServiceName)
                        .AddTelemetrySdk())
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(config.Endpoint);
                    });

                if (config.EnableConsoleExporter)
                {
                    loggerProviderBuilder.AddConsoleExporter();
                }
            });

        return services;
    }
}
