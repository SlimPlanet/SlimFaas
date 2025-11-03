using System.Net;
using DotNext;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.AspNetCore.Connections;
using SlimData.Commands;

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
        const string ListLeftPushResource = "/SlimData/ListLeftPush";
        const string ListLeftPushBatchResource = "/SlimData/ListLeftPushBatch";
        const string AddKeyValueResource = "/SlimData/AddKeyValue";
        const string ListLengthResource = "/SlimData/ListLength";
        const string ListCallback = "/SlimData/ListCallback";
        const string ListCallBackBatch = "/SlimData/ListCallbackBatch";
        const string HealthResource = "/health";
#pragma warning disable DOTNEXT001
     //   app.RestoreStateAsync<SlimPersistentState>(new CancellationToken());
#pragma warning restore DOTNEXT001
        app.UseConsensusProtocolHandler()
            .RedirectToLeader(LeaderResource)
            .RedirectToLeader(ListLengthResource)
            .RedirectToLeader(ListLeftPushResource)
            .RedirectToLeader(ListLeftPushBatchResource)
            .RedirectToLeader(ListRightPopResource)
            .RedirectToLeader(AddKeyValueResource)
            .RedirectToLeader(AddHashSetResource)
            .RedirectToLeader(ListCallback)
            .RedirectToLeader(ListCallBackBatch)
            .UseRouting()
            .UseEndpoints(static endpoints =>
            {
                endpoints.MapGet(LeaderResource, Endpoints.RedirectToLeaderAsync);
                endpoints.MapGet(HealthResource, async context => { await context.Response.WriteAsync("OK"); });
                endpoints.MapPost(ListLeftPushResource,  Endpoints.ListLeftPushAsync);
                endpoints.MapPost(ListLeftPushBatchResource,  Endpoints.ListLeftPushBatchAsync);
                endpoints.MapPost(ListRightPopResource,  Endpoints.ListRightPopAsync);
                endpoints.MapPost(AddHashSetResource,  Endpoints.AddHashSetAsync);
                endpoints.MapPost(DeleteHashSetResource,  Endpoints.DeleteHashSetAsync);
                endpoints.MapPost(AddKeyValueResource,  Endpoints.AddKeyValueAsync);
                endpoints.MapPost(ListCallback,  Endpoints.ListCallbackAsync);
                endpoints.MapPost(ListCallBackBatch,  Endpoints.ListCallbackBatchAsync);
            });
    }

    public void ConfigureServices(IServiceCollection services)
    {
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
        services.UseInMemoryConfigurationStorage(AddClusterMembers)
            .ConfigureCluster<ClusterConfigurator>()
            .AddSingleton<IHttpMessageHandlerFactory, RaftClientHandlerFactory>()
            .AddOptions()
            .AddRouting();
        var path = configuration[SlimPersistentState.LogLocation];
       /* if (!string.IsNullOrWhiteSpace(path))
        {
#pragma warning disable DOTNEXT001
            services.AddSingleton(new WriteAheadLog.Options { Location = path });
            services.UseStateMachine<SlimPersistentState>();
#pragma warning restore DOTNEXT001
        }*/
       if (!string.IsNullOrWhiteSpace(path))
           services.UsePersistenceEngine<ISupplier<SlimDataPayload>, SlimPersistentState>();
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