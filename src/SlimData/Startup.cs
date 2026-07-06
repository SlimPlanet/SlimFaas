using System.Net;
using DotNext;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
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
            .UseRouting()
            .UseEndpoints(static endpoints =>
            {
                endpoints.MapClusterFileTransferRoutes();
                endpoints.MapGet(LeaderResource, Endpoints.RedirectToLeaderAsync);
                endpoints.MapGet(HealthResource, async context => { await context.Response.WriteAsync("OK"); });
                endpoints.MapPost(ListLeftPushBatchResource,  Endpoints.ListLeftPushBatchAsync);
                endpoints.MapPost(ListRightPopResource,  Endpoints.ListRightPopAsync);
                endpoints.MapPost(AddHashSetResource,  Endpoints.AddHashSetAsync);
                endpoints.MapPost(DeleteHashSetResource,  Endpoints.DeleteHashSetAsync);
                endpoints.MapPost(AddKeyValueBatchResource,  Endpoints.AddKeyValueBatchAsync);
                endpoints.MapPost(ListCallback,  Endpoints.ListCallbackAsync);
                endpoints.MapPost(ListCallBackBatch,  Endpoints.ListCallbackBatchAsync);
            });
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // Configure RaftClientHandler options
        services.AddOptions<RaftClientHandlerOptions>()
            .Bind(configuration.GetSection(RaftClientHandlerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

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
            services.UsePersistentConfigurationStorage(configPath)
                .ConfigureCluster<ClusterConfigurator>()
                .AddSingleton<IHttpMessageHandlerFactory, RaftClientHandlerFactory>()
                .AddOptions()
                .AddRouting();
        else
            services.UseInMemoryConfigurationStorage(AddClusterMembers)
                .ConfigureCluster<ClusterConfigurator>()
                .AddSingleton<IHttpMessageHandlerFactory, RaftClientHandlerFactory>()
                .AddOptions()
                .AddRouting();

        services
            .UseStateMachine<SlimPersistentState>(new WriteAheadLog.Options { Location = walPath })
            .AddSingleton<ISupplier<SlimDataPayload>>(sp => sp.GetRequiredService<SlimPersistentState>());

        if (!string.IsNullOrWhiteSpace(path))
        {
           services.AddSingleton<IFileRepository>(sp => new DiskFileRepository(
               Path.Combine(stateRoot, "files"),
               sp.GetRequiredService<ILogger<DiskFileRepository>>()));
           services.AddSingleton<ClusterFileAnnounceQueue>();
           services.AddHostedService<ClusterFileAnnounceWorker>();

           services.AddSingleton<IClusterFileSync, ClusterFileSync>(); 
           services.AddHostedService<ClusterFileSyncBootstrapper>();
        }
           
        var endpoint = configuration["publicEndPoint"];
        if (!string.IsNullOrEmpty(endpoint))
        {
            var uri = new Uri(endpoint);
            services.AddSingleton<SlimDataInfo>(sp => new SlimDataInfo(uri.Port));
        }
    }

    private static void AddClusterMembers(ICollection<UriEndPoint> members)
    {
        foreach (var clusterMember in ClusterMembers)
            members.Add(new UriEndPoint(new Uri(clusterMember, UriKind.Absolute)));
    }
}
