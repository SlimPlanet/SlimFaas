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
        if (!config.Enable)
        {
            return services;
        }

        var resourceBuilder = ResourceBuilder.CreateDefault().AddTelemetrySdk();
        if (!string.IsNullOrWhiteSpace(config.ServiceName))
        {
            resourceBuilder.AddService(config.ServiceName);
        }

        services.AddOpenTelemetry()
            .WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .SetResourceBuilder(resourceBuilder);

                if (!string.IsNullOrWhiteSpace(config.ServiceName))
                {
                    tracerProviderBuilder.AddSource(config.ServiceName);
                }

                tracerProviderBuilder.AddOtlpExporter(
                    !string.IsNullOrWhiteSpace(config.Endpoint)
                        ? options => options.Endpoint = new Uri(config.Endpoint)
                        : _ => { }
                );

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
                    .SetResourceBuilder(resourceBuilder);

                if (!string.IsNullOrWhiteSpace(config.ServiceName))
                {
                    meterProviderBuilder.AddMeter(config.ServiceName);
                }

                meterProviderBuilder.AddOtlpExporter(
                    !string.IsNullOrWhiteSpace(config.Endpoint)
                        ? options => options.Endpoint = new Uri(config.Endpoint)
                        : _ => { }
                );

                if (config.EnableConsoleExporter)
                {
                    meterProviderBuilder.AddConsoleExporter();
                }
            })
            .WithLogging(loggerProviderBuilder =>
            {
                loggerProviderBuilder.SetResourceBuilder(resourceBuilder);
                loggerProviderBuilder.AddOtlpExporter(
                    !string.IsNullOrWhiteSpace(config.Endpoint)
                        ? options => options.Endpoint = new Uri(config.Endpoint)
                        : _ => { }
                );

                if (config.EnableConsoleExporter)
                {
                    loggerProviderBuilder.AddConsoleExporter();
                }
            });

        return services;
    }
}
