using OpenTelemetry;
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

        var resourceBuilder = CreateResourceBuilder(config.ServiceName);

        services.AddOpenTelemetry()
            .WithTracing(tracerProviderBuilder => ConfigureTracing(config, tracerProviderBuilder, resourceBuilder))
            .WithMetrics(meterProviderBuilder => ConfigureMetric(config, meterProviderBuilder, resourceBuilder))
            .WithLogging(loggerProviderBuilder => ConfigureLogging(config, loggerProviderBuilder, resourceBuilder));

        return services;
    }

    private static ResourceBuilder CreateResourceBuilder(string? serviceName)
    {
        var builder = ResourceBuilder.CreateDefault().AddTelemetrySdk();

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            builder.AddService(serviceName);
        }

        return builder;
    }

    private static void ConfigureTracing(OpenTelemetryConfig config, TracerProviderBuilder tracerProviderBuilder,
        ResourceBuilder resourceBuilder)
    {
        tracerProviderBuilder
            .AddAspNetCoreInstrumentation(options =>
            {
                if (config.ExcludedUrls is { Length: > 0 })
                {
                    options.Filter = httpContext =>
                    {
                        var path = httpContext.Request.Path.Value ?? string.Empty;
                        foreach (var excludedUrl in config.ExcludedUrls)
                        {
                            if (path.StartsWith(excludedUrl, StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }
                        }
                        return true;
                    };
                }
            })
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
    }

    private static void ConfigureMetric(OpenTelemetryConfig config, MeterProviderBuilder meterProviderBuilder,
        ResourceBuilder resourceBuilder)
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
    }

    private static void ConfigureLogging(OpenTelemetryConfig config, LoggerProviderBuilder loggerProviderBuilder,
        ResourceBuilder resourceBuilder)
    {
        loggerProviderBuilder.SetResourceBuilder(resourceBuilder);

        loggerProviderBuilder.AddOtlpExporter(
            !string.IsNullOrWhiteSpace(config.Endpoint)
                ? options => options.Endpoint = new Uri(config.Endpoint)
                : _ => { }
        );

        if (config.ExcludedUrls is { Length: > 0 })
        {
            loggerProviderBuilder.AddProcessor(new ExcludeUrlLogProcessor(config.ExcludedUrls));
        }

        if (config.EnableConsoleExporter)
        {
            loggerProviderBuilder.AddConsoleExporter();
        }
    }
}

internal class ExcludeUrlLogProcessor(string[] excludedUrls) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        if (ShouldExclude(data))
        {
            return;
        }

        base.OnEnd(data);
    }

    private bool ShouldExclude(LogRecord data)
    {
        if (data.Attributes == null)
        {
            return false;
        }

        var path = data.Attributes.FirstOrDefault(kvp =>
            kvp.Key == "Path" || kvp.Key == "RequestPath" || kvp.Key == "url.path")
            .Value?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var excludedUrl in excludedUrls)
        {
            if (path.StartsWith(excludedUrl, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
