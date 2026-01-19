using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Prometheus;
using SlimData;
using SlimData.Expiration;
using SlimFaas;
using SlimFaas.Configuration;
using SlimFaas.Database;
using SlimFaas.Extensions;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.Workers;
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
serviceCollectionStarter.AddSingleton<IJobConfiguration, JobConfiguration>();

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
var usePersistentConfigurationStorage = true;
switch (envOrConfig)
{
    case "Docker":
        serviceCollectionStarter
            .AddHttpClient(DockerService.HttpClientName, client =>
            {
                // Host "factice" requis pour HttpClient, pas utilisé avec le ConnectCallback.
                client.BaseAddress = new Uri("http://docker");
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                // Chemin par défaut à l'intérieur du conteneur
                var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
                var udsPath = "/var/run/docker.sock";

                if (!string.IsNullOrWhiteSpace(dockerHost) &&
                    dockerHost.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
                {
                    udsPath = dockerHost["unix://".Length..]; // on enlève le prefix "unix://"
                }

                return new SocketsHttpHandler
                {
                    ConnectCallback = async (ctx, ct) =>
                    {
                        var ep   = new UnixDomainSocketEndPoint(udsPath);
                        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                        try
                        {
                            await sock.ConnectAsync(ep, ct);
                        }
                        catch (SocketException ex)
                        {
                            throw new HttpRequestException(
                                $"Failed to connect to Docker/Podman unix socket '{udsPath}'. " +
                                "Vérifie que le socket est monté dans le conteneur et que les droits sont OK.",
                                ex);
                        }

                        return new NetworkStream(sock, ownsSocket: true);
                    }
                };
            })
            .ConfigureAdditionalHttpMessageHandlers((_, _) => { });

        serviceCollectionStarter.AddSingleton<IKubernetesService, DockerService>();
        serviceCollectionStarter.RemoveAll<IHttpMessageHandlerBuilderFilter>();
        usePersistentConfigurationStorage = false;
        break;

    case "Mock":
        serviceCollectionStarter.AddSingleton<IKubernetesService, MockKubernetesService>();
        break;
    default:
        serviceCollectionStarter.AddSingleton<IKubernetesService, KubernetesService>(sp =>
        {
            bool useKubeConfig = bool.Parse(configuration["UseKubeConfig"] ?? "false");
            return new KubernetesService(sp.GetRequiredService<ILogger<KubernetesService>>(), useKubeConfig, sp.GetRequiredService<IJobConfiguration>());
        });
        break;
}

// Store métriques Prometheus en mémoire
serviceCollectionStarter.AddSingleton<IMetricsStore, InMemoryMetricsStore>();

// Evaluateur PromQL branché sur le snapshot du store
serviceCollectionStarter.AddSingleton<PromQlMiniEvaluator>(sp =>
{
    var store = (InMemoryMetricsStore)sp.GetRequiredService<IMetricsStore>();
    return new PromQlMiniEvaluator(store.Snapshot);
});

// Store d’historique de décisions de scaling
serviceCollectionStarter.AddSingleton<IAutoScalerStore, InMemoryAutoScalerStore>();

// AutoScaler (utilisé par ReplicasService)
serviceCollectionStarter.AddSingleton<AutoScaler>();
serviceCollectionStarter.AddSingleton<IRequestedMetricsRegistry, RequestedMetricsRegistry>();
serviceCollectionStarter.AddSingleton<IMetricsScrapingGuard, MetricsScrapingGuard>();
serviceCollectionStarter.AddSingleton<IMetricsStore, InMemoryMetricsStore>();

ServiceProvider serviceProviderStarter = serviceCollectionStarter.BuildServiceProvider();
IReplicasService? replicasService = serviceProviderStarter.GetService<IReplicasService>();

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

var openTelemetryConfig = builder.Configuration
    .GetSection("OpenTelemetry")
    .Get<OpenTelemetryConfig>() ?? new OpenTelemetryConfig();

builder.Services.AddOpenTelemetry(openTelemetryConfig);

IServiceCollection serviceCollectionSlimFaas = builder.Services;
// Réutilise IMetricsStore / PromQlMiniEvaluator / AutoScaler depuis le starter
serviceCollectionSlimFaas.AddSingleton<IMetricsStore>(sp =>
    serviceProviderStarter.GetRequiredService<IMetricsStore>());

serviceCollectionSlimFaas.AddSingleton<PromQlMiniEvaluator>(sp =>
    serviceProviderStarter.GetRequiredService<PromQlMiniEvaluator>());

serviceCollectionSlimFaas.AddSingleton<IAutoScalerStore>(sp =>
    serviceProviderStarter.GetRequiredService<IAutoScalerStore>());

serviceCollectionSlimFaas.AddSingleton<AutoScaler>(sp =>
    serviceProviderStarter.GetRequiredService<AutoScaler>());
serviceCollectionSlimFaas.AddHostedService<SlimQueuesWorker>();
serviceCollectionSlimFaas.AddHostedService<SlimScheduleJobsWorker>();
serviceCollectionSlimFaas.AddHostedService<SlimJobsWorker>();
serviceCollectionSlimFaas.AddHostedService<SlimJobsConfigurationWorker>();
serviceCollectionSlimFaas.AddHostedService<ScaleReplicasWorker>();

serviceCollectionSlimFaas.AddHostedService<ReplicasSynchronizationWorker>();
serviceCollectionSlimFaas.AddHostedService<HistorySynchronizationWorker>();
serviceCollectionSlimFaas.AddHostedService<MetricsWorker>();
serviceCollectionSlimFaas.AddHostedService<HealthWorker>();
serviceCollectionSlimFaas.AddHostedService<MetricsScrapingWorker>();
serviceCollectionSlimFaas.AddHttpClient();
serviceCollectionSlimFaas.AddSingleton<ISlimFaasQueue, SlimFaasQueue>();
serviceCollectionSlimFaas.AddSingleton<DynamicGaugeService>();
serviceCollectionSlimFaas.AddSingleton<ISlimDataStatus, SlimDataStatus>();
serviceCollectionSlimFaas.AddSingleton<IReplicasService, ReplicasService>(sp =>
    (ReplicasService)serviceProviderStarter.GetService<IReplicasService>()!);
serviceCollectionSlimFaas.AddSingleton<IMetricsScrapingGuard>(sp =>
    serviceProviderStarter.GetRequiredService<IMetricsScrapingGuard>());
serviceCollectionSlimFaas.AddSingleton<IRequestedMetricsRegistry>(sp =>
    serviceProviderStarter.GetRequiredService<IRequestedMetricsRegistry>());
serviceCollectionSlimFaas.AddSingleton<IMetricsStore>(sp =>
    serviceProviderStarter.GetRequiredService<IMetricsStore>());
serviceCollectionSlimFaas.AddSingleton<ISlimFaasPorts, SlimFaasPorts>(sp =>
    (SlimFaasPorts)serviceProviderStarter.GetService<ISlimFaasPorts>()!);
serviceCollectionSlimFaas.AddSingleton<HistoryHttpDatabaseService>();
serviceCollectionSlimFaas.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>(sp =>
    serviceProviderStarter.GetService<HistoryHttpMemoryService>()!);
serviceCollectionSlimFaas.AddSingleton<IKubernetesService>(sp =>
    serviceProviderStarter.GetService<IKubernetesService>()!);
serviceCollectionSlimFaas.AddSingleton<IJobService, JobService>();
serviceCollectionSlimFaas.AddSingleton<IJobQueue, JobQueue>();
serviceCollectionSlimFaas.AddSingleton<IJobConfiguration>(sp =>
    serviceProviderStarter.GetService<IJobConfiguration>()!);
serviceCollectionSlimFaas.AddSingleton<IScheduleJobService, ScheduleJobService>();
serviceCollectionSlimFaas.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();

builder.Services
    .AddOptions<DataOptions>()
    .BindConfiguration(DataOptions.SectionName)
    .Validate(o => o.DefaultVisibility is FunctionVisibility.Public or FunctionVisibility.Private,
        "Data:DefaultVisibility must be Public or Private.");

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

if (replicasService?.Deployments?.SlimFaas?.Pods.Count == 1)
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
    if (!Directory.Exists(podDataDirectoryPersistantStorage))
    {
        Directory.CreateDirectory(podDataDirectoryPersistantStorage);
    }
    else
    {
        switch (envOrConfig)
        {
            case "Docker":
                Directory.Delete(podDataDirectoryPersistantStorage, true);
                Directory.CreateDirectory(podDataDirectoryPersistantStorage);
                break;
        }
    }

    publicEndPoint = SlimDataEndpoint.Get(currentPod);
    Console.WriteLine($"Node started {currentPod.Name} {publicEndPoint}");
}



// -------------------------------------------------------------
// Détermination du mode coldStart en fonction de l'état sur disque
// et du rang du pod (StatefulSet : slimfaas-0, slimfaas-1, ...)
// -------------------------------------------------------------
bool hasExistingState =
    !string.IsNullOrEmpty(podDataDirectoryPersistantStorage) &&
    Directory.EnumerateFileSystemEntries(podDataDirectoryPersistantStorage).Any();

// Avec un StatefulSet, le premier pod est typiquement ...-0
bool isFirstPod = hostname.EndsWith("-0", StringComparison.OrdinalIgnoreCase);

Console.WriteLine($"SlimData state dir: {podDataDirectoryPersistantStorage}, hasExistingState={hasExistingState}, isFirstPod={isFirstPod}, slimDataAllowColdStart={slimDataAllowColdStart}");

// Règle :
// - Si on a déjà un état sur disque -> on ne fait PAS de cold start.
// - Si répertoire vide -> seul le premier pod (…-0) peut booter le cluster
//   et seulement si slimDataAllowColdStart = true (config globale).
string coldStart = (!hasExistingState && isFirstPod && slimDataAllowColdStart) ? "true" : "false";
if(envOrConfig=="Docker") {
	coldStart = "true";
}

Dictionary<string, string> slimDataDefaultConfiguration = new()
{
    { "partitioning", "false" },
    { "lowerElectionTimeout", "1500" },
    { "upperElectionTimeout", "3000" },
    { "requestTimeout", "00:00:02.5000000" },
    { "rpcTimeout", "00:00:01.0000000" },
    { "publicEndPoint", publicEndPoint },
    { "coldStart", coldStart },
    { "requestJournal:memoryLimit", "30" },
    { "requestJournal:expiration", "00:01:30" },
    { "heartbeatThreshold", "0.25" }
};


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
	builder.Configuration[SlimPersistentState.UsePersistentConfigurationStorage] = usePersistentConfigurationStorage.ToString();
} else {
	builder.Configuration[SlimPersistentState.UsePersistentConfigurationStorage] = false.ToString();
}

Startup startup = new(builder.Configuration);

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

serviceCollectionSlimFaas.AddSingleton<SlimDataExpirationCleaner>();
serviceCollectionSlimFaas.AddHostedService(sp =>
    new SlimDataExpirationCleanupWorker(
        sp.GetRequiredService<SlimDataExpirationCleaner>(),
        sp.GetRequiredService<ILogger<SlimDataExpirationCleanupWorker>>(),
        interval: TimeSpan.FromSeconds(30)));


builder.Host
    .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(slimDataConfiguration!))
    .JoinCluster();

Uri uri = new(publicEndPoint);
var slimfaasPorts = serviceProviderStarter.GetService<ISlimFaasPorts>();

var maxRequestBodySize = builder.Configuration.GetValue<long?>("Kestrel:Limits:MaxRequestBodySize")
                         ?? 524_288_000L;

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxRequestBodySize;
});
builder.WebHost.ConfigureKestrel((context, serverOptions) =>
{
    serverOptions.Configure(context.Configuration.GetSection("Kestrel"));
    serverOptions.Limits.MaxRequestBodySize = maxRequestBodySize;
    serverOptions.ListenAnyIP(uri.Port, lo =>
    {
        lo.Protocols = HttpProtocols.Http1;
        lo.KestrelServerOptions.Limits.MaxRequestBodySize = maxRequestBodySize;
    });

    if (slimfaasPorts == null)
    {
        Console.WriteLine("No Slimfaas ports");
        return;
    }
    Console.WriteLine("Initializing Slimfaas ports");
    foreach (int slimFaasPort in slimfaasPorts.Ports.Where(p => p != uri.Port))
    {
        Console.WriteLine($"Slimfaas listening on port {slimFaasPort}");
        serverOptions.ListenAnyIP(slimFaasPort, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
            listenOptions.KestrelServerOptions.Limits.MaxRequestBodySize = maxRequestBodySize;
        });
    }
});

// AOT-friendly JSON (source generators)
builder.Services.ConfigureHttpJsonOptions(opt =>
{
    var serializerOptionsTypeInfoResolverChain = opt.SerializerOptions.TypeInfoResolverChain;
    serializerOptionsTypeInfoResolverChain.Insert(0, AppJsonContext.Default);
    serializerOptionsTypeInfoResolverChain.Insert(1, DataFileRoutesJsonContext.Default);
    serializerOptionsTypeInfoResolverChain.Insert(2, DataSetFileRoutesRoutesJsonContext.Default);
    serializerOptionsTypeInfoResolverChain.Insert(3, DataHashsetFileRoutesJsonContext.Default);
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

//app.MapDataHashsetRoutes();
app.MapDataSetRoutes();
app.MapDataFileRoutes();
app.MapDebugRoutes();

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
