using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Prometheus;
using SlimData;
using SlimFaas;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using EnvironmentVariables = SlimFaas.EnvironmentVariables;

#pragma warning disable CA2252

var namespace_ = Namespace.GetNamespace();
Console.WriteLine($"Starting in namespace {namespace_}");

string slimDataDirectory = Environment.GetEnvironmentVariable(EnvironmentVariables.SlimDataDirectory) ??
                           EnvironmentVariables.GetTemporaryDirectory();

string? slimDataConfigurationString =  Environment.GetEnvironmentVariable(EnvironmentVariables.SlimDataConfiguration) ?? "";
DictionnaryString slimDataConfiguration= new();

if (!string.IsNullOrEmpty(slimDataConfigurationString))
{
    slimDataConfigurationString = JsonMinifier.MinifyJson(slimDataConfigurationString);
    if (!string.IsNullOrEmpty(slimDataConfigurationString))
    {
        var dictionnaryDeserialize = JsonSerializer.Deserialize(slimDataConfigurationString,
            DictionnaryStringSerializerContext.Default.DictionnaryString);
        if (dictionnaryDeserialize != null)
        {
            slimDataConfiguration = dictionnaryDeserialize;
        }
    }
}

const string coldstart = "coldStart";
bool slimDataAllowColdStart =
    bool.Parse(slimDataConfiguration.GetValueOrDefault(coldstart) ??
                                                                EnvironmentVariables.SlimDataAllowColdStartDefault.ToString());

ServiceCollection serviceCollectionStarter = new();
serviceCollectionStarter.AddSingleton<IReplicasService, ReplicasService>();
serviceCollectionStarter.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
serviceCollectionStarter.AddSingleton<ISlimFaasPorts, SlimFaasPorts>();

string? environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{environment}.json", true)
    .AddEnvironmentVariables().Build();

serviceCollectionStarter.AddLogging(loggingBuilder =>
{
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
});
var envOrConfig = Environment.GetEnvironmentVariable(EnvironmentVariables.SlimFaasOrchestrator) ?? EnvironmentVariables.SlimFaasOrchestratorDefault;
Console.WriteLine($"Using orchestrator: {envOrConfig}");

switch (envOrConfig)
{
    case "Docker":
        serviceCollectionStarter.AddHttpClient(DockerService.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("http://localhost"); // obligatoire pour HttpClient
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
                var udsPath = "/var/run/docker.sock";
                if (!string.IsNullOrWhiteSpace(dockerHost) && dockerHost.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
                    udsPath = dockerHost.Replace("unix://", "", StringComparison.OrdinalIgnoreCase);

                return new SocketsHttpHandler
                {
                    ConnectCallback = async (ctx, ct) =>
                    {
                        var ep = new UnixDomainSocketEndPoint(udsPath);
                        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        await sock.ConnectAsync(ep, ct);
                        return new NetworkStream(sock, ownsSocket: true);
                    }
                };
            }).ConfigureAdditionalHttpMessageHandlers((_, _) => { });
        serviceCollectionStarter.AddSingleton<IKubernetesService, DockerService>();
        serviceCollectionStarter.RemoveAll<IHttpMessageHandlerBuilderFilter>();
        break;
    case "Mock":
        serviceCollectionStarter.AddSingleton<IKubernetesService, MockKubernetesService>();
        break;
    default:
        serviceCollectionStarter.AddSingleton<IKubernetesService, KubernetesService>(sp =>
        {
            bool useKubeConfig = bool.Parse(configuration["UseKubeConfig"] ?? "false");
            return new KubernetesService(sp.GetRequiredService<ILogger<KubernetesService>>(), useKubeConfig);
        });
        break;
}

ServiceProvider serviceProviderStarter = serviceCollectionStarter.BuildServiceProvider();
IReplicasService? replicasService = serviceProviderStarter.GetService<IReplicasService>();

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

IServiceCollection serviceCollectionSlimFaas = builder.Services;
serviceCollectionSlimFaas.AddHostedService<SlimQueuesWorker>();
serviceCollectionSlimFaas.AddHostedService<SlimScheduleJobsWorker>();
serviceCollectionSlimFaas.AddHostedService<SlimJobsWorker>();
serviceCollectionSlimFaas.AddHostedService<ScaleReplicasWorker>();

serviceCollectionSlimFaas.AddHostedService<ReplicasSynchronizationWorker>();
serviceCollectionSlimFaas.AddHostedService<HistorySynchronizationWorker>();
serviceCollectionSlimFaas.AddHostedService<MetricsWorker>();
serviceCollectionSlimFaas.AddHostedService<HealthWorker>();
serviceCollectionSlimFaas.AddHostedService<ThreadPoolTuner>();
serviceCollectionSlimFaas.AddHttpClient();
serviceCollectionSlimFaas.AddSingleton<ISlimFaasQueue, SlimFaasQueue>();
serviceCollectionSlimFaas.AddSingleton<DynamicGaugeService>();
serviceCollectionSlimFaas.AddSingleton<ISlimDataStatus, SlimDataStatus>();
serviceCollectionSlimFaas.AddSingleton<IReplicasService, ReplicasService>(sp =>
    (ReplicasService)serviceProviderStarter.GetService<IReplicasService>()!);
serviceCollectionSlimFaas.AddSingleton<ISlimFaasPorts, SlimFaasPorts>(sp =>
    (SlimFaasPorts)serviceProviderStarter.GetService<ISlimFaasPorts>()!);
serviceCollectionSlimFaas.AddSingleton<HistoryHttpDatabaseService>();
serviceCollectionSlimFaas.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>(sp =>
    serviceProviderStarter.GetService<HistoryHttpMemoryService>()!);
serviceCollectionSlimFaas.AddSingleton<IKubernetesService>(sp =>
    serviceProviderStarter.GetService<IKubernetesService>()!);
serviceCollectionSlimFaas.AddSingleton<IJobService, JobService>();
serviceCollectionSlimFaas.AddSingleton<IJobQueue, JobQueue>();
serviceCollectionSlimFaas.AddSingleton<IJobConfiguration, JobConfiguration>();
serviceCollectionSlimFaas.AddSingleton<IScheduleJobService, ScheduleJobService>();

serviceCollectionSlimFaas.AddCors();

string publicEndPoint = string.Empty;
string podDataDirectoryPersistantStorage = string.Empty;

replicasService?.SyncDeploymentsAsync(namespace_).Wait();
string hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? EnvironmentVariables.HostnameDefault;

while (replicasService?.Deployments?.SlimFaas?.Pods.Any(p => p.Name.Contains(hostname)) == false)
{
    foreach (PodInformation podInformation in replicasService?.Deployments?.SlimFaas?.Pods ?? Array.Empty<PodInformation>())
    {
        Console.WriteLine($"Current SlimFaas pod: {podInformation.Name} {podInformation.Ip} {podInformation.Started}");
    }
    Console.WriteLine("Waiting current pod to be ready");
    Task.Delay(1000).Wait();
    replicasService?.SyncDeploymentsAsync(namespace_).Wait();
}

if (replicasService?.Deployments?.SlimFaas?.Pods.Count == 1 && !Directory.EnumerateDirectories(slimDataDirectory).Any())
{
    slimDataAllowColdStart = true;
    Console.WriteLine($"Starting SlimFaas, coldstart:{slimDataAllowColdStart}");
}

while (!slimDataAllowColdStart &&
       replicasService?.Deployments?.SlimFaas?.Pods.Count(p => !string.IsNullOrEmpty(p.Ip)) < 2)
{
    Console.WriteLine("Waiting for at least 2 pods to be ready");
    Task.Delay(1000).Wait();
    replicasService?.SyncDeploymentsAsync(namespace_).Wait();
}

if (replicasService?.Deployments?.SlimFaas?.Pods != null)
{
    /*foreach (string enumerateDirectory in Directory.EnumerateDirectories(slimDataDirectory))
    {
        if (replicasService.Deployments.SlimFaas.Pods.Any(p =>
                (new DirectoryInfo(enumerateDirectory).Name).Contains(p.Name)) == false)
        {
            try
            {
                Console.WriteLine($"Deleting {enumerateDirectory}");
                Directory.Delete(enumerateDirectory, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
    }*/

    foreach (PodInformation podInformation in replicasService.Deployments.SlimFaas.Pods
                 .Where(p => !string.IsNullOrEmpty(p.Ip) && p.Started == true).ToList())
    {
        try
        {
            string slimDataEndpoint = SlimDataEndpoint.Get(podInformation);
            if (!podInformation.Name.Contains(hostname))
            {
                Console.WriteLine($"Adding node  {slimDataEndpoint} {hostname} {podInformation.Name}");
                Startup.AddClusterMemberBeforeStart(slimDataEndpoint);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding node {ex}");
        }
    }

    PodInformation currentPod = replicasService.Deployments.SlimFaas.Pods.First(p => p.Name.Contains(hostname));
    Console.WriteLine($"Starting node {currentPod.Name}");
    podDataDirectoryPersistantStorage = Path.Combine(slimDataDirectory, currentPod.Name);
    if (Directory.Exists(podDataDirectoryPersistantStorage) == false)
    {
        Directory.CreateDirectory(podDataDirectoryPersistantStorage);
    }

    publicEndPoint = SlimDataEndpoint.Get(currentPod);
    Console.WriteLine($"Node started {currentPod.Name} {publicEndPoint}");
}

var allowUnsecureSSL = EnvironmentVariables.ReadBoolean(EnvironmentVariables.SlimFaasAllowUnsecureSSL, EnvironmentVariables.SlimFaasAllowUnsecureSSLDefault);

serviceCollectionSlimFaas.AddHostedService<SlimDataSynchronizationWorker>();
serviceCollectionSlimFaas.AddSingleton<IDatabaseService, SlimDataService>();
serviceCollectionSlimFaas.AddSingleton<IWakeUpFunction, WakeUpFunction>();
serviceCollectionSlimFaas.AddHttpClient(SlimDataService.HttpClientName)
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        client.DefaultRequestVersion = HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var httpClientHandler = new HttpClientHandler { AllowAutoRedirect = true };
        if (allowUnsecureSSL)
        {
            httpClientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        }

        return httpClientHandler;
    });
// Export metrics from all HTTP clients registered in services
builder.Services.UseHttpClientMetrics();

serviceCollectionSlimFaas.AddSingleton<IMasterService, MasterSlimDataService>();

serviceCollectionSlimFaas.AddScoped<ISendClient, SendClient>();
serviceCollectionSlimFaas.AddHttpClient<ISendClient, SendClient>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var httpClientHandler = new HttpClientHandler
        {
            AllowAutoRedirect = true
        };
        if (allowUnsecureSSL)
        {
            httpClientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        }

        return httpClientHandler;
    });

if (!string.IsNullOrEmpty(podDataDirectoryPersistantStorage))
{
    builder.Configuration[SlimPersistentState.LogLocation] = podDataDirectoryPersistantStorage;
}

Startup startup = new(builder.Configuration);
// Node start as master if it is alone in the cluster
string coldStart = replicasService != null && replicasService?.Deployments?.SlimFaas?.Pods.Count == 1 ? "true" : "false";

Dictionary<string, string> slimDataDefaultConfiguration = new()
{
    { "partitioning", "false" },
    { "lowerElectionTimeout", "2500" },
    { "upperElectionTimeout", "5000" },
    { "requestTimeout", "00:00:05.0000000" },
    { "rpcTimeout", "00:00:02.5000000" },
    { "publicEndPoint", publicEndPoint },
    { coldstart, coldStart },
    { "requestJournal:memoryLimit", "50" },
    { "requestJournal:expiration", "00:05:00" },
    { "heartbeatThreshold", "0.5" }
};
foreach (KeyValuePair<string,string> keyValuePair in slimDataDefaultConfiguration)
{
    if (!slimDataConfiguration.ContainsKey(keyValuePair.Key))
    {
        slimDataConfiguration.Add(keyValuePair.Key, keyValuePair.Value);
    }
}
foreach (KeyValuePair<string,string> keyValuePair in slimDataConfiguration)
{
    Console.WriteLine($"- {keyValuePair.Key}:{keyValuePair.Value}");
}

builder.Configuration["publicEndPoint"] = slimDataConfiguration["publicEndPoint"];
startup.ConfigureServices(serviceCollectionSlimFaas);

builder.Host
    .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(slimDataConfiguration!))
    .JoinCluster();

Uri uri = new(publicEndPoint);
var slimfaasPorts = serviceProviderStarter.GetService<ISlimFaasPorts>();
builder.WebHost.ConfigureKestrel((context, serverOptions) =>
{
    serverOptions.Limits.MaxRequestBodySize = EnvironmentVariables.ReadLong<long>(null, EnvironmentVariables.SlimFaasMaxRequestBodySize, EnvironmentVariables.SlimFaasMaxRequestBodySizeDefault);
    serverOptions.ListenAnyIP(uri.Port, o => o.Protocols = HttpProtocols.Http1);

    if (slimfaasPorts == null)
    {
        Console.WriteLine("No Slimfaas ports");
        return;
    }
    Console.WriteLine("Initializing Slimfaas ports");
    foreach (int slimFaasPort in slimfaasPorts.Ports)
    {
        Console.WriteLine($"Slimfaas listening on port {slimFaasPort}");
        serverOptions.ListenAnyIP(slimFaasPort, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
        });
    }
});


WebApplication app = builder.Build();
app.UseCors(builder =>
{
    string slimFaasCorsAllowOrigin = Environment.GetEnvironmentVariable(EnvironmentVariables.SlimFaasCorsAllowOrigin) ??
                               EnvironmentVariables.SlimFaasCorsAllowOriginDefault;
    if (slimFaasCorsAllowOrigin == "*")
    {
        Console.WriteLine("CORS Allowing all origins");
        builder
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    }
    else
    {
        builder
            .WithOrigins(slimFaasCorsAllowOrigin.Split(','))
            .AllowAnyMethod()
            .AllowAnyHeader();
    }
});

app.UseMiddleware<SlimProxyMiddleware>();

app.Use(async (context, next) =>
{

    if (slimfaasPorts == null)
    {
        await next.Invoke();
        return;
    }
    if (!HostPort.IsSamePort([context.Connection.LocalPort, context.Request.Host.Port ?? 0], slimfaasPorts.Ports.ToArray()))
    {
        await next.Invoke();
        return;
    }

    if (context.Request.Path == "/health")
    {
        await context.Response.WriteAsync("OK");
    }
    else if (context.Request.Path == "/ready")
    {
        var cluster = context.RequestServices.GetService<IRaftCluster>();
        // 200 si le nœud a terminé son warmup/rattrapage et peut être ajouté
        if (cluster is not null && cluster.Readiness.IsCompletedSuccessfully)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("READY");
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("NOT_READY");
        }
        return;
    }
    else
    {
        await next.Invoke();
    }
});

app.UseRouting();
app.UseHttpMetrics(options =>
{
    // This will preserve only the first digit of the status code.
    // For example: 200, 201, 203 -> 2xx
    options.ReduceStatusCodeCardinality();
});
app.UseMetricServer();
startup.Configure(app);

app.Run(async context =>
{
    context.Response.StatusCode = 404;
    await context.Response.WriteAsync("404");
});

app.Run();
serviceProviderStarter.Dispose();

public partial class Program;


#pragma warning restore CA2252
