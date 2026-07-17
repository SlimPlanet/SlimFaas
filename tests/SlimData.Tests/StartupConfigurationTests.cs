using System.Text;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Configuration;

namespace SlimData.Tests;

public sealed class StartupConfigurationTests
{
    [Fact]
    public void Wal_memory_management_defaults_to_private_memory()
    {
        var configuration = new ConfigurationBuilder().Build();

        var strategy = Startup.GetWalMemoryManagement(configuration);

        Assert.Equal(WriteAheadLog.MemoryManagementStrategy.PrivateMemory, strategy);
    }

    [Theory]
    [InlineData("PrivateMemory", WriteAheadLog.MemoryManagementStrategy.PrivateMemory)]
    [InlineData("privatememory", WriteAheadLog.MemoryManagementStrategy.PrivateMemory)]
    [InlineData("SharedMemory", WriteAheadLog.MemoryManagementStrategy.SharedMemory)]
    [InlineData("sHaReDmEmOrY", WriteAheadLog.MemoryManagementStrategy.SharedMemory)]
    public void Wal_memory_management_reads_json_case_insensitively(
        string configuredValue,
        WriteAheadLog.MemoryManagementStrategy expected)
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""
            {
              "SlimData": {
                "WalMemoryManagement": "{{configuredValue}}"
              }
            }
            """));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(json)
            .Build();

        var strategy = Startup.GetWalMemoryManagement(configuration);

        Assert.Equal(expected, strategy);
    }

    [Fact]
    public void Wal_memory_management_environment_variable_overrides_json()
    {
        var prefix = $"SLIMDATA_WAL_TEST_{Guid.NewGuid():N}_";
        var variable = $"{prefix}SlimData__WalMemoryManagement";

        try
        {
            Environment.SetEnvironmentVariable(variable, "SharedMemory");
            using var json = new MemoryStream(Encoding.UTF8.GetBytes(
                """
                {
                  "SlimData": {
                    "WalMemoryManagement": "PrivateMemory"
                  }
                }
                """));
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(json)
                .AddEnvironmentVariables(prefix)
                .Build();

            var strategy = Startup.GetWalMemoryManagement(configuration);

            Assert.Equal(WriteAheadLog.MemoryManagementStrategy.SharedMemory, strategy);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("42")]
    [InlineData("ManagedMemory")]
    public void Wal_memory_management_rejects_invalid_values(string configuredValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [Startup.WalMemoryManagement] = configuredValue
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => Startup.GetWalMemoryManagement(configuration));

        Assert.Contains(Startup.WalMemoryManagement, exception.Message, StringComparison.Ordinal);
        Assert.Contains("PrivateMemory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SharedMemory", exception.Message, StringComparison.Ordinal);
    }
}
