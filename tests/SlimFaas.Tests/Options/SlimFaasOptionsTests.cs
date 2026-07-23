using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SlimFaas.Options;

namespace SlimFaas.Tests.Options;

public class SlimFaasOptionsTests
{
    [Fact]
    public void SlimFaasOptions_ShouldBindFromConfiguration()
    {
        // Arrange
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SlimFaas:Namespace"] = "test-namespace",
            ["SlimFaas:CorsAllowOrigin"] = "https://test.com",
            ["SlimFaas:AllowUnsecureSsl"] = "true",
            ["SlimFaas:Orchestrator"] = "Docker"
        });
        var configuration = configurationBuilder.Build();

        var services = new ServiceCollection();
        services.AddOptions<SlimFaasOptions>()
            .BindConfiguration(SlimFaasOptions.SectionName);
        services.AddSingleton<IConfiguration>(configuration);

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetRequiredService<IOptions<SlimFaasOptions>>().Value;

        // Assert
        Assert.Equal("test-namespace", options.Namespace);
        Assert.Equal("https://test.com", options.CorsAllowOrigin);
        Assert.True(options.AllowUnsecureSsl);
        Assert.Equal("Docker", options.Orchestrator);
    }

    [Fact]
    public void SlimFaasOptions_ShouldUseDefaultValues()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddOptions<SlimFaasOptions>()
            .BindConfiguration(SlimFaasOptions.SectionName);
        services.AddSingleton<IConfiguration>(configuration);

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetRequiredService<IOptions<SlimFaasOptions>>().Value;

        // Assert
        Assert.Equal("default", options.Namespace);
        Assert.Equal("*", options.CorsAllowOrigin);
        Assert.False(options.AllowUnsecureSsl);
        Assert.Equal("Kubernetes", options.Orchestrator);
        Assert.Equal(1000, options.StatusStream.StateIntervalMilliseconds);
        Assert.Equal(1000, options.StatusStream.QueueLengthsCacheMilliseconds);
        Assert.Equal(1000, options.StatusStream.JobsCacheMilliseconds);
        Assert.Equal(2000, options.StatusStream.PeerSyncIntervalMilliseconds);
        Assert.Equal(5000, options.StatusStream.PeerSyncInitialDelayMilliseconds);
        Assert.Equal(0, options.StatusStream.MaxSseClients);
        Assert.Equal(10000, options.StatusStream.SubscriberChannelCapacity);
        Assert.Equal(1000, options.StatusStream.RecentActivityLimit);
        Assert.Equal(10000, options.StatusStream.KnownIdsLimit);
        Assert.Equal(0, options.StatusStream.MaxLiveEventsPerSecond);
        Assert.Equal(1.0, options.StatusStream.LiveEventSamplingRatio);
        Assert.Equal(100, options.StatusStream.LiveActivityBatchSize);
        Assert.Equal(2_000, options.MetricsScraping.ScrapeIntervalMilliseconds);
        Assert.Equal(8L * 1024L * 1024L, options.MetricsScraping.MaxResponseBytes);
        Assert.Equal(64 * 1024, options.MetricsScraping.MaxLineBytes);
        Assert.Equal(10_000, options.MetricsScraping.MaxSelectedSeriesPerTarget);
        Assert.Equal(10, options.MetricsScraping.RequestTimeoutSeconds);
    }

    [Fact]
    public void SlimFaasOptions_MetricsScraping_ShouldBindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlimFaas:MetricsScraping:ScrapeIntervalMilliseconds"] = "3000",
                ["SlimFaas:MetricsScraping:MaxResponseBytes"] = "16777216",
                ["SlimFaas:MetricsScraping:MaxLineBytes"] = "131072",
                ["SlimFaas:MetricsScraping:MaxSelectedSeriesPerTarget"] = "20000",
                ["SlimFaas:MetricsScraping:RequestTimeoutSeconds"] = "20"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<SlimFaasOptions>().BindConfiguration(SlimFaasOptions.SectionName);
        services.AddSingleton<IConfiguration>(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<SlimFaasOptions>>().Value.MetricsScraping;

        Assert.Equal(3_000, options.ScrapeIntervalMilliseconds);
        Assert.Equal(16L * 1024L * 1024L, options.MaxResponseBytes);
        Assert.Equal(128 * 1024, options.MaxLineBytes);
        Assert.Equal(20_000, options.MaxSelectedSeriesPerTarget);
        Assert.Equal(20, options.RequestTimeoutSeconds);
    }

    [Theory]
    [InlineData("ScrapeIntervalMilliseconds", "0")]
    [InlineData("MaxResponseBytes", "0")]
    [InlineData("MaxLineBytes", "0")]
    [InlineData("MaxLineBytes", "9000000")]
    [InlineData("MaxSelectedSeriesPerTarget", "0")]
    [InlineData("RequestTimeoutSeconds", "0")]
    public void SlimFaasOptions_MetricsScraping_ShouldRejectInvalidValues(string setting, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"SlimFaas:MetricsScraping:{setting}"] = value
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSlimFaasOptions(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            serviceProvider.GetRequiredService<IOptions<SlimFaasOptions>>().Value);
    }

    [Fact]
    public void SlimFaasOptions_MetricsScraping_SupportsEnvironmentVariableOverride()
    {
        var prefix = $"SLIMFAAS_METRICS_TEST_{Guid.NewGuid():N}_";
        var variable = $"{prefix}SlimFaas__MetricsScraping__MaxResponseBytes";
        Environment.SetEnvironmentVariable(variable, "123456");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables(prefix)
                .Build();
            var services = new ServiceCollection();
            services.AddOptions<SlimFaasOptions>().BindConfiguration(SlimFaasOptions.SectionName);
            services.AddSingleton<IConfiguration>(configuration);

            using var serviceProvider = services.BuildServiceProvider();
            var options = serviceProvider.GetRequiredService<IOptions<SlimFaasOptions>>().Value;

            Assert.Equal(123456, options.MetricsScraping.MaxResponseBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void SlimFaasOptions_StatusStream_ShouldBindFromConfiguration()
    {
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SlimFaas:StatusStream:StateIntervalMilliseconds"] = "2500",
            ["SlimFaas:StatusStream:QueueLengthsCacheMilliseconds"] = "3000",
            ["SlimFaas:StatusStream:JobsCacheMilliseconds"] = "4000",
            ["SlimFaas:StatusStream:PeerSyncIntervalMilliseconds"] = "5000",
            ["SlimFaas:StatusStream:PeerSyncInitialDelayMilliseconds"] = "6000",
            ["SlimFaas:StatusStream:MaxSseClients"] = "7",
            ["SlimFaas:StatusStream:SubscriberChannelCapacity"] = "800",
            ["SlimFaas:StatusStream:RecentActivityLimit"] = "90",
            ["SlimFaas:StatusStream:KnownIdsLimit"] = "100",
            ["SlimFaas:StatusStream:MaxLiveEventsPerSecond"] = "11",
            ["SlimFaas:StatusStream:LiveEventSamplingRatio"] = "0.5",
            ["SlimFaas:StatusStream:LiveActivityBatchSize"] = "25"
        });
        var configuration = configurationBuilder.Build();

        var services = new ServiceCollection();
        services.AddOptions<SlimFaasOptions>()
            .BindConfiguration(SlimFaasOptions.SectionName);
        services.AddSingleton<IConfiguration>(configuration);

        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<SlimFaasOptions>>().Value.StatusStream;

        Assert.Equal(2500, options.StateIntervalMilliseconds);
        Assert.Equal(3000, options.QueueLengthsCacheMilliseconds);
        Assert.Equal(4000, options.JobsCacheMilliseconds);
        Assert.Equal(5000, options.PeerSyncIntervalMilliseconds);
        Assert.Equal(6000, options.PeerSyncInitialDelayMilliseconds);
        Assert.Equal(7, options.MaxSseClients);
        Assert.Equal(800, options.SubscriberChannelCapacity);
        Assert.Equal(90, options.RecentActivityLimit);
        Assert.Equal(100, options.KnownIdsLimit);
        Assert.Equal(11, options.MaxLiveEventsPerSecond);
        Assert.Equal(0.5, options.LiveEventSamplingRatio);
        Assert.Equal(25, options.LiveActivityBatchSize);
    }

    [Fact]
    public void WorkersOptions_ShouldBindFromConfiguration()
    {
        // Arrange
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workers:QueuesDelayMilliseconds"] = "20",
            ["Workers:JobsDelayMilliseconds"] = "2000",
            ["Workers:HealthDelayMilliseconds"] = "500"
        });
        var configuration = configurationBuilder.Build();

        var services = new ServiceCollection();
        services.AddOptions<WorkersOptions>()
            .BindConfiguration(WorkersOptions.SectionName);
        services.AddSingleton<IConfiguration>(configuration);

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetRequiredService<IOptions<WorkersOptions>>().Value;

        // Assert
        Assert.Equal(20, options.QueuesDelayMilliseconds);
        Assert.Equal(2000, options.JobsDelayMilliseconds);
        Assert.Equal(500, options.HealthDelayMilliseconds);
    }

    [Fact]
    public void SlimDataOptions_ShouldBindFromConfiguration()
    {
        // Arrange
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SlimData:Directory"] = "/custom/path",
            ["SlimData:AllowColdStart"] = "true",
            ["SlimData:WarmupRounds"] = "20000",
            ["SlimData:Configuration"] = "{\"test\":\"value\"}"
        });
        var configuration = configurationBuilder.Build();

        var services = new ServiceCollection();
        services.AddOptions<SlimDataOptions>()
            .BindConfiguration(SlimDataOptions.SectionName);
        services.AddSingleton<IConfiguration>(configuration);

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetRequiredService<IOptions<SlimDataOptions>>().Value;

        // Assert
        Assert.Equal("/custom/path", options.Directory);
        Assert.True(options.AllowColdStart);
        Assert.Equal(20_000, options.WarmupRounds);
        Assert.Equal("{\"test\":\"value\"}", options.Configuration);
    }

    [Fact]
    public void OptionsExtensions_AddSlimFaasOptions_ShouldRegisterAllOptions()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        // Act
        services.AddSlimFaasOptions(configuration);
        var serviceProvider = services.BuildServiceProvider();

        // Assert - All options should be registered
        var slimFaasOptions = serviceProvider.GetService<IOptions<SlimFaasOptions>>();
        var slimDataOptions = serviceProvider.GetService<IOptions<SlimDataOptions>>();
        var workersOptions = serviceProvider.GetService<IOptions<WorkersOptions>>();

        Assert.NotNull(slimFaasOptions);
        Assert.NotNull(slimDataOptions);
        Assert.NotNull(workersOptions);
    }

    [Fact]
    public void OptionsExtensions_GetTemporaryDirectory_ShouldReturnValidPath()
    {
        // Act
        var tempDir = OptionsExtensions.GetTemporaryDirectory();

        // Assert
        Assert.False(string.IsNullOrEmpty(tempDir));
        Assert.True(Directory.Exists(tempDir));

        // Cleanup
        try
        {
            Directory.Delete(tempDir);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void SlimFaasOptions_SupportsEnvironmentVariableOverride()
    {
        // Arrange
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SlimFaas:Namespace"] = "config-namespace"
        });
        // Simulate environment variable override
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SlimFaas:Namespace"] = "env-namespace" // Override
        });
        var configuration = configurationBuilder.Build();

        var services = new ServiceCollection();
        services.AddOptions<SlimFaasOptions>()
            .BindConfiguration(SlimFaasOptions.SectionName);
        services.AddSingleton<IConfiguration>(configuration);

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetRequiredService<IOptions<SlimFaasOptions>>().Value;

        // Assert - Environment variable should override config file
        Assert.Equal("env-namespace", options.Namespace);
    }
}
