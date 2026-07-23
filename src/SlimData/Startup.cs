using System.Net;
using DotNext;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.AspNetCore.Connections;
using SlimData.ClusterFiles;
using SlimData.ClusterFiles.Http;
using SlimData.Commands;
using SlimData.Options;

namespace SlimData;

public class Startup(IConfiguration configuration)
{
    private static readonly IList<string> ClusterMembers = new List<string>(2);
    private static Uri? LocalEndpoint;
    internal const string MembershipAnnounceResource = "/SlimData/members/announce";
    internal const string ProtocolResource = "/SlimData/protocol";
    internal const string WalMemoryManagement = "SlimData:WalMemoryManagement";

    public static void AddClusterMemberBeforeStart(string endpoint)
    {
        ClusterMembers.Add(endpoint);
    }
    
    public void Configure(IApplicationBuilder app)
    {
        const string LeaderResource = "/SlimData/leader";
        const string AddHashSetResource = "/SlimData/AddHashset";
        const string DeleteHashSetResource = "/SlimData/DeleteHashset";
        const string ListRightPopResource = "/SlimData/ListRightPop";
        const string ListLeftPushBatchResource = "/SlimData/ListLeftPushBatch";
        const string AddKeyValueBatchResource = "/SlimData/AddKeyValueBatch";
        const string ListLengthResource = "/SlimData/ListLength";
        const string ListCallback = "/SlimData/ListCallback";
        const string ListCallBackBatch = "/SlimData/ListCallbackBatch";
        const string HealthResource = "/health";
        app.RestoreStateAsync<SlimPersistentState>().GetAwaiter().GetResult();
       
        app.UseConsensusProtocolHandler()
            .RedirectToLeader(LeaderResource)
            .RedirectToLeader(ListLengthResource)
            .RedirectToLeader(ListLeftPushBatchResource)
            .RedirectToLeader(ListRightPopResource)
            .RedirectToLeader(AddKeyValueBatchResource)
            .RedirectToLeader(AddHashSetResource)
            .RedirectToLeader(ListCallback)
            .RedirectToLeader(ListCallBackBatch)
            .RedirectToLeader(MembershipAnnounceResource)
            .UseRouting()
            .UseEndpoints(static endpoints =>
            {
                endpoints.MapClusterFileTransferRoutes();
                endpoints.MapGet(LeaderResource, Endpoints.RedirectToLeaderAsync);
                endpoints.MapGet(HealthResource, async context => { await context.Response.WriteAsync("OK"); });
                endpoints.MapGet(ProtocolResource, Endpoints.ProtocolAsync);
                endpoints.MapPost(ListLeftPushBatchResource,  Endpoints.ListLeftPushBatchAsync);
                endpoints.MapPost(ListRightPopResource,  Endpoints.ListRightPopAsync);
                endpoints.MapPost(AddHashSetResource,  Endpoints.AddHashSetAsync);
                endpoints.MapPost(DeleteHashSetResource,  Endpoints.DeleteHashSetAsync);
                endpoints.MapPost(AddKeyValueBatchResource,  Endpoints.AddKeyValueBatchAsync);
                endpoints.MapPost(ListCallback,  Endpoints.ListCallbackAsync);
                endpoints.MapPost(ListCallBackBatch,  Endpoints.ListCallbackBatchAsync);
                endpoints.MapPost(MembershipAnnounceResource, Endpoints.AnnounceMemberAsync);
            });
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.PostConfigure<HttpClusterMemberConfiguration>(options =>
            options.IsLeaderLeaseEnabled = true);

        // Configure RaftClientHandler options
        services.AddOptions<RaftClientHandlerOptions>()
            .Bind(configuration.GetSection(RaftClientHandlerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SlimDataMembershipOptions>()
            .Bind(configuration.GetSection(SlimDataMembershipOptions.SectionName))
            .Validate(
                options => options.ChangeTimeoutSeconds > 0,
                "SlimData membership change timeout must be positive.")
            .Validate(
                options => options.AnnouncementTimeoutSeconds > options.ChangeTimeoutSeconds,
                "SlimData membership announcement timeout must exceed the change timeout.")
            .Validate(
                options => options.RemovalMissingCycles > 0,
                "SlimData membership removal missing cycles must be positive.")
            .ValidateOnStart();
        services.AddClusterFileOptions(configuration);
        services.AddSingleton<ClusterMembershipCoordinator>();
        services.AddSingleton<IClusterMembershipCoordinator>(sp =>
            sp.GetRequiredService<ClusterMembershipCoordinator>());
        services.AddSingleton<SlimDataProtocolCompatibility>();
        services.AddSingleton<ISlimDataProtocolCompatibility>(sp =>
            sp.GetRequiredService<SlimDataProtocolCompatibility>());
        services.AddHostedService<SlimDataProtocolCompatibilityWorker>();

        services.AddHttpClient(SlimDataProtocolClient.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestVersion = HttpVersion.Version11;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                MaxConnectionsPerServer = 4,
                UseProxy = false
            });

        services.AddHttpClient("ClusterFilesTransfer", c =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false 
            });

        services.AddHttpClient("RaftClient", c =>
            {
                c.DefaultRequestVersion = HttpVersion.Version11;
                c.DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionExact;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 100,
                EnableMultipleHttp2Connections = false,
                UseProxy = false
            });
        var path = configuration[SlimPersistentState.LogLocation];
        var stateRoot = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Path.GetTempPath(), "SlimData", Guid.NewGuid().ToString("N"))
            : path;
        var walPath = Path.Combine(stateRoot, "wal");
        var configPath = Path.Combine(stateRoot, "config");
        var usePersistentConfigurationStorage = bool.Parse(configuration[SlimPersistentState.UsePersistentConfigurationStorage] ?? "true");
        if (!string.IsNullOrWhiteSpace(path) && usePersistentConfigurationStorage)
        {
            services.UsePersistentConfigurationStorage(configPath)
                .ConfigureCluster<ClusterConfigurator>()
                .AddSingleton<IHttpMessageHandlerFactory, RaftClientHandlerFactory>()
                .AddOptions()
                .AddRouting();
            services.AddHttpClient(ClusterMembershipAnnouncer.HttpClientName, client =>
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    client.DefaultRequestVersion = HttpVersion.Version11;
                    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                    MaxConnectionsPerServer = 4,
                    UseProxy = false
                });
            services.AddSingleton<ClusterMembershipAnnouncer>();
            services.AddSingleton<ClusterMemberAnnouncer<UriEndPoint>>(sp =>
                sp.GetRequiredService<ClusterMembershipAnnouncer>().AnnounceAsync);
            services.AddHostedService<ClusterMembershipAnnounceWorker>();
        }
        else
            services.UseInMemoryConfigurationStorage(AddClusterMembers)
                .ConfigureCluster<ClusterConfigurator>()
                .AddSingleton<IHttpMessageHandlerFactory, RaftClientHandlerFactory>()
                .AddOptions()
                .AddRouting();

        services
            .UseStateMachine<SlimPersistentState>(new WriteAheadLog.Options
            {
                Location = walPath,
                MemoryManagement = GetWalMemoryManagement(configuration)
            })
            .AddSingleton<ISupplier<SlimDataPayload>>(sp => sp.GetRequiredService<SlimPersistentState>());

        if (!string.IsNullOrWhiteSpace(path))
        {
           services.AddSingleton<IFileRepository>(sp => new DiskFileRepository(
               Path.Combine(stateRoot, "files"),
               sp.GetRequiredService<ILogger<DiskFileRepository>>(),
               sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ClusterFileOptions>>().Value.DropPageCache));
           services.AddSingleton<ClusterFileAnnounceQueue>();
           services.AddHostedService<ClusterFileAnnounceWorker>();

           services.AddSingleton<IClusterFileSync, ClusterFileSync>(); 
           services.AddHostedService<ClusterFileSyncBootstrapper>();
        }
           
        var endpoint = configuration["publicEndPoint"];
        if (!string.IsNullOrEmpty(endpoint))
        {
            var uri = new Uri(endpoint);
            LocalEndpoint = uri;
            services.AddSingleton<SlimDataInfo>(sp => new SlimDataInfo(uri.Port));
        }
    }

    private static void AddClusterMembers(ICollection<UriEndPoint> members)
    {
        foreach (var clusterMember in ClusterMembers)
            members.Add(new UriEndPoint(new Uri(clusterMember, UriKind.Absolute)));
    }

    internal static WriteAheadLog.MemoryManagementStrategy GetWalMemoryManagement(IConfiguration configuration)
    {
        var configuredValue = configuration[WalMemoryManagement];
        if (configuredValue is null)
            return WriteAheadLog.MemoryManagementStrategy.SharedMemory;

        if (string.Equals(
                configuredValue,
                nameof(WriteAheadLog.MemoryManagementStrategy.PrivateMemory),
                StringComparison.OrdinalIgnoreCase))
        {
            return WriteAheadLog.MemoryManagementStrategy.PrivateMemory;
        }

        if (string.Equals(
                configuredValue,
                nameof(WriteAheadLog.MemoryManagementStrategy.SharedMemory),
                StringComparison.OrdinalIgnoreCase))
        {
            return WriteAheadLog.MemoryManagementStrategy.SharedMemory;
        }

        throw new InvalidOperationException(
            $"Invalid value '{configuredValue}' for '{WalMemoryManagement}'. " +
            "Allowed values are 'PrivateMemory' and 'SharedMemory'.");
    }

    internal static Uri[] GetKnownClusterMembers()
        => ClusterMembers.Select(static endpoint => new Uri(endpoint, UriKind.Absolute)).ToArray();

    internal static bool IsAllowedClusterMember(Uri candidate)
    {
        if (GetKnownClusterMembers().Any(known => SameEndpoint(known, candidate)))
            return true;

        var local = LocalEndpoint;
        if (local is null || !string.Equals(local.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase) ||
            local.Port != candidate.Port)
        {
            return false;
        }

        var localLabels = local.Host.Split('.');
        var candidateLabels = candidate.Host.Split('.');
        if (localLabels.Length < 2 || candidateLabels.Length != localLabels.Length ||
            !localLabels.Skip(1).SequenceEqual(candidateLabels.Skip(1), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(RemoveOrdinal(localLabels[0]), RemoveOrdinal(candidateLabels[0]), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SameEndpoint(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
           left.Port == right.Port;

    private static string RemoveOrdinal(string hostLabel)
    {
        var separator = hostLabel.LastIndexOf('-');
        return separator > 0 && int.TryParse(hostLabel.AsSpan(separator + 1), out _)
            ? hostLabel[..separator]
            : hostLabel;
    }
}
