using System.Text;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SlimData.Options;

namespace SlimData.Tests;

public sealed class StartupConfigurationTests
{
    [Fact]
    public async Task Resolving_the_wal_before_snapshot_restore_fails_instead_of_replaying_partial_state()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(
                Path.Combine(AppContext.BaseDirectory, "Snapshots", "dotnext-6.6.0-wal.zip"), root);
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [SlimPersistentState.LogLocation] = root }).Build();
            var services = new ServiceCollection();
            services.AddSingleton(configuration).AddLogging();
            new Startup(configuration).ConfigureServices(services);
            await using ServiceProvider provider = services.BuildServiceProvider();

            // DotNext 6.8.1 guards this contract for every DI consumer, including new hosts.
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => provider.GetRequiredService<IPersistentState>());
            Assert.Contains("restor", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(provider.GetRequiredService<SlimPersistentState>().SlimDataState.KeyValues);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Membership_defaults_allow_slow_recovery()
    {
        var options = new SlimDataMembershipOptions();

        Assert.Equal(180, options.ChangeTimeoutSeconds);
        Assert.Equal(200, options.AnnouncementTimeoutSeconds);
    }

    [Fact]
    public void Wal_memory_management_defaults_to_shared_memory()
    {
        var configuration = new ConfigurationBuilder().Build();

        var strategy = Startup.GetWalMemoryManagement(configuration);

        Assert.Equal(WriteAheadLog.MemoryManagementStrategy.SharedMemory, strategy);
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

    [Fact]
    public void Raft_client_handler_options_accept_default_values()
    {
        var options = new RaftClientHandlerOptions();

        Assert.True(Startup.ValidateRaftClientHandlerOptions(options));
    }

    [Theory]
    [InlineData(0, 5, 30, 100)]
    [InlineData(2000, 0, 30, 100)]
    [InlineData(2000, 5, 0, 100)]
    [InlineData(2000, 5, 30, 0)]
    public void Raft_client_handler_options_reject_non_positive_values(
        int connectTimeoutMilliseconds,
        int pooledConnectionLifetimeMinutes,
        int pooledConnectionIdleTimeoutSeconds,
        int maxConnectionsPerServer)
    {
        var options = new RaftClientHandlerOptions
        {
            ConnectTimeoutMilliseconds = connectTimeoutMilliseconds,
            PooledConnectionLifetimeMinutes = pooledConnectionLifetimeMinutes,
            PooledConnectionIdleTimeoutSeconds = pooledConnectionIdleTimeoutSeconds,
            MaxConnectionsPerServer = maxConnectionsPerServer
        };

        Assert.False(Startup.ValidateRaftClientHandlerOptions(options));
    }
}
