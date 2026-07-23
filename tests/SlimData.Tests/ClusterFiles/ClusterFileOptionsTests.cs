using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SlimData.Options;

namespace SlimData.Tests.ClusterFiles;

public sealed class ClusterFileOptionsTests
{
    [Fact]
    public void Defaults_match_the_bounded_memory_profile()
    {
        var options = new ClusterFileOptions();

        Assert.Equal(256L * 1024L * 1024L, options.MaxInFlightBytes);
        Assert.Equal(256L * 1024L * 1024L, options.UnknownLengthReservationBytes);
        Assert.Equal(128, options.MaxPendingTransfers);
        Assert.Equal(30, options.QueueWaitTimeoutSeconds);
        Assert.True(options.DropPageCache);
    }

    [Fact]
    public void Options_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlimData:Files:MaxInFlightBytes"] = "1024",
                ["SlimData:Files:UnknownLengthReservationBytes"] = "2048",
                ["SlimData:Files:MaxPendingTransfers"] = "7",
                ["SlimData:Files:QueueWaitTimeoutSeconds"] = "9",
                ["SlimData:Files:DropPageCache"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddClusterFileOptions(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ClusterFileOptions>>().Value;

        Assert.Equal(1024, options.MaxInFlightBytes);
        Assert.Equal(2048, options.UnknownLengthReservationBytes);
        Assert.Equal(7, options.MaxPendingTransfers);
        Assert.Equal(9, options.QueueWaitTimeoutSeconds);
        Assert.False(options.DropPageCache);
    }

    [Theory]
    [InlineData("MaxInFlightBytes")]
    [InlineData("UnknownLengthReservationBytes")]
    [InlineData("MaxPendingTransfers")]
    [InlineData("QueueWaitTimeoutSeconds")]
    public void Options_reject_non_positive_limits(string setting)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"SlimData:Files:{setting}"] = "0"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddClusterFileOptions(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ClusterFileOptions>>().Value);
    }

    [Fact]
    public void Environment_variable_overrides_file_options()
    {
        var prefix = $"SLIMDATA_FILES_TEST_{Guid.NewGuid():N}_";
        var variable = $"{prefix}SlimData__Files__MaxPendingTransfers";
        Environment.SetEnvironmentVariable(variable, "23");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables(prefix)
                .Build();
            var services = new ServiceCollection();
            services.AddClusterFileOptions(configuration);

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<ClusterFileOptions>>().Value;

            Assert.Equal(23, options.MaxPendingTransfers);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
