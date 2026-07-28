using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SlimFaas.Kubernetes;

namespace SlimFaas.Local;

public sealed class LocalSupervisor(LoadedLocalManifest loaded, bool clean)
{
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        EnsureClusterPortsAvailable();
        using LocalStateStore state = LocalStateStore.Open(loaded, clean);
        var allocator = new LocalPortAllocator(loaded, state);
        await using var functions = new LocalFunctionManager(loaded, allocator, state);
        await using var jobs = new LocalJobManager(loaded, state);
<<<<<<< HEAD
        await using var processes = new LocalProcessManager(loaded, allocator, state);
        allocator.Prune(functions.PortAllocationIds
            .Concat(processes.PortAllocationIds)
            .ToHashSet(StringComparer.Ordinal));
        processes.PreparePorts();
=======
>>>>>>> origin/main

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(LocalSupervisor).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ProcessControlJsonContext.Default));

        WebApplication control = builder.Build();
        LocalNodeManager? nodes = null;

        control.Use(async (context, next) =>
        {
            if (!context.Request.Headers.TryGetValue(
                    ProcessKubernetesService.TokenHeaderName,
                    out Microsoft.Extensions.Primitives.StringValues supplied) ||
                !TokensEqual(supplied.ToString(), _token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        control.MapGet("/v1/topology", (string? @namespace) =>
        {
            string namespaceName = string.IsNullOrWhiteSpace(@namespace) ? loaded.Manifest.Name : @namespace;
            IList<DeploymentInformation> functionSnapshot = functions.Snapshot(namespaceName);
            IList<PodInformation> nodeSnapshot = nodes?.Snapshot() ?? CreateInitialNodeSnapshot();
            var topology = new DeploymentsInformations(
                Functions: functionSnapshot,
                SlimFaas: new SlimFaasDeploymentInformation(loaded.Manifest.Cluster.Nodes, nodeSnapshot),
                Pods: functionSnapshot.SelectMany(item => item.Pods).ToList());
            return Results.Json(topology, ProcessControlJsonContext.Default.DeploymentsInformations);
        });

        control.MapPut(
            "/v1/functions/{name}/scale",
            async (string name, ProcessScaleCommand command, CancellationToken token) =>
            {
                ReplicaRequest? result = await functions.ScaleAsync(
                    new ReplicaRequest(name, loaded.Manifest.Name, command.Replicas, PodType.Deployment),
                    token);
                return result is null
                    ? Results.NotFound()
                    : Results.NoContent();
            });

        control.MapGet(
            "/v1/jobs/configuration",
            () => Results.Json(
                jobs.Configuration,
                ProcessControlJsonContext.Default.SlimFaasJobConfiguration));

        control.MapPost(
            "/v1/jobs",
            async (ProcessCreateJobCommand command, CancellationToken token) =>
            {
                try
                {
                    await jobs.CreateAsync(command, token);
                    return Results.Accepted();
                }
                catch (LocalManifestException exception)
                {
                    return Results.BadRequest(new ProcessControlError(exception.Message));
                }
            });

        control.MapGet(
            "/v1/jobs",
            () => Results.Json(
                jobs.Snapshot(),
                ProcessControlJsonContext.Default.ListJob));

        control.MapDelete(
            "/v1/jobs/{name}",
            async (string name, CancellationToken token) =>
            {
                await jobs.DeleteAsync(name, token);
                return Results.NoContent();
            });

        await control.StartAsync(cancellationToken);
        Uri controlUri = GetControlUri(control);
        var nodeManager = new LocalNodeManager(loaded, state, controlUri, _token);
        nodes = nodeManager;
        LocalTcpGateway? gateway = null;
        try
        {
<<<<<<< HEAD
            await Task.WhenAll(
                functions.StartAsync(cancellationToken),
                jobs.StartAsync(cancellationToken),
                processes.StartAsync(cancellationToken),
                nodeManager.StartAsync(cancellationToken));
=======
            await functions.StartAsync(cancellationToken);
            await jobs.StartAsync(cancellationToken);
            await nodeManager.StartAsync(cancellationToken);
>>>>>>> origin/main
            gateway = new LocalTcpGateway(loaded.Manifest.Cluster.GatewayPort, nodeManager);
            gateway.Start();

            Console.WriteLine(
                $"SlimFaas local '{loaded.Manifest.Name}' is running with {loaded.Manifest.Cluster.Nodes} node(s).");
<<<<<<< HEAD
            Console.WriteLine($"Auxiliary processes: {loaded.Manifest.Processes.Count}");
=======
>>>>>>> origin/main
            Console.WriteLine($"Gateway: http://127.0.0.1:{loaded.Manifest.Cluster.GatewayPort}");
            Console.WriteLine($"State: {loaded.StateDirectory}");
            Console.WriteLine("Press Ctrl+C to stop.");

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            if (gateway is not null)
                await gateway.DisposeAsync();
            await nodeManager.DisposeAsync();
            await control.StopAsync(CancellationToken.None);
            await control.DisposeAsync();
        }
    }

    private IList<PodInformation> CreateInitialNodeSnapshot()
        => Enumerable.Range(0, loaded.Manifest.Cluster.Nodes)
            .Select(index => new PodInformation(
                Name: $"slimfaas-{index}",
                Started: false,
                Ready: false,
                Ip: "127.0.0.1",
                DeploymentName: "slimfaas",
                Ports:
                [
                    loaded.Manifest.Cluster.RaftPortBase + index,
                    loaded.Manifest.Cluster.HttpPortBase + index
                ],
                ServiceName: "slimfaas"))
            .ToList();

    private void EnsureClusterPortsAvailable()
    {
        var ports = new Dictionary<int, string>
        {
            [loaded.Manifest.Cluster.GatewayPort] = "cluster.gatewayPort"
        };
        for (var index = 0; index < loaded.Manifest.Cluster.Nodes; index++)
        {
            ports[loaded.Manifest.Cluster.HttpPortBase + index] = $"cluster.httpPortBase[{index}]";
            ports[loaded.Manifest.Cluster.RaftPortBase + index] = $"cluster.raftPortBase[{index}]";
        }

        foreach ((int port, string setting) in ports)
        {
            if (!LocalManifestLoader.IsTcpPortAvailable(port))
                throw new LocalManifestException($"{setting} port {port} is already in use.");
        }
    }

    private static Uri GetControlUri(WebApplication application)
    {
        IServer server = application.Services.GetRequiredService<IServer>();
        string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                         ?? throw new InvalidOperationException(
                             "The local supervisor did not publish a control address.");
        return new Uri(address);
    }

    private static bool TokensEqual(string supplied, string expected)
    {
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
