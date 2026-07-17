using System.Net;
using DotNext.Net.Cluster.Consensus.Raft.Http;

namespace SlimData;

public class Starter
{
    private static IServiceProvider ServiceProvider { get; set; } = null!;

    private static Task UseAspNetCoreHost(string publicEndPoint, 
        string? persistentStorage = null)
    {
        var uri = new Uri(publicEndPoint);

        var configuration = new Dictionary<string, string?>
        {
            { "partitioning", "false" },
            { "lowerElectionTimeout", "300" },
            { "upperElectionTimeout", "600" },
            { "warmupRounds", "10000" },
            { "publicEndPoint", publicEndPoint },
            { "coldStart", "false" },
            { "requestJournal:memoryLimit", "10" },
            { "requestJournal:expiration", "00:00:15" },
            { "heartbeatThreshold", "0.6" }
        };
        if (!string.IsNullOrEmpty(persistentStorage))
            configuration[SlimPersistentState.LogLocation] = persistentStorage;

        var host = new HostBuilder().ConfigureWebHost(webHost =>
            {
                webHost.UseKestrel(options =>
                    {
                        ServiceProvider = options.ApplicationServices;
                        options.Listen(IPAddress.Loopback, uri.Port);
                    })
                    .UseStartup<Startup>();
            })
            .ConfigureLogging(ConfigureLogging)
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration))
            .JoinCluster()
            .Build();

        return host.RunAsync();
    }

    private static void ConfigureLogging(ILoggingBuilder builder)
    {
        builder.AddConsole().SetMinimumLevel(LogLevel.Warning);
    }

    public static Task StartNode(string publicEndPoint = "http://localhost:3262", string? persistentStorage = null)
    {
        return UseAspNetCoreHost(publicEndPoint, persistentStorage);
    }
}
