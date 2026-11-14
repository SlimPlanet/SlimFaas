using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimData.ClusterFiles;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Endpoints;

public class AllowedCallersEndpointTests
{
    private const string FrontendIp = "10.0.0.1";
    private const string BackofficeIp = "10.0.0.2";
    private const string BatchImportIp = "10.0.0.3";
    private const string UntrustedIp = "10.0.0.4";
    private const string JobIp = "10.42.0.17";
    private const string ExternalIp = "203.0.113.7";

    [Theory]
    [InlineData("/function/payments/charge", FrontendIp, HttpStatusCode.OK)]
    [InlineData("/function/payments/charge", BatchImportIp, HttpStatusCode.OK)]
    [InlineData("/function/payments/charge", JobIp, HttpStatusCode.OK)]
    [InlineData("/function/payments/charge", BackofficeIp, HttpStatusCode.Forbidden)]
    [InlineData("/function/payments/charge", UntrustedIp, HttpStatusCode.NotFound)]
    [InlineData("/function/payments/charge", ExternalIp, HttpStatusCode.NotFound)]
    [InlineData("/function/payments/health", BackofficeIp, HttpStatusCode.OK)]
    [InlineData("/function/payments/health", ExternalIp, HttpStatusCode.OK)]
    [InlineData("/function/ledger/entries", BackofficeIp, HttpStatusCode.OK)]
    [InlineData("/async-function/payments/charge", FrontendIp, HttpStatusCode.Accepted)]
    [InlineData("/async-function/payments/charge", JobIp, HttpStatusCode.Accepted)]
    [InlineData("/async-function/payments/charge", BackofficeIp, HttpStatusCode.Forbidden)]
    [InlineData("/async-function/payments/charge", ExternalIp, HttpStatusCode.NotFound)]
    [InlineData("/async-function/payments/health", BackofficeIp, HttpStatusCode.Accepted)]
    [InlineData("/async-function/ledger/entries", BackofficeIp, HttpStatusCode.Accepted)]
    public async Task PrivateRoutes_AreRestrictedToTheAllowedCallers(string path, string remoteIp, HttpStatusCode expected)
    {
        using IHost host = await StartHostAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:5000{path}");
        request.Headers.TryAddWithoutValidation(TestRemoteIp.HeaderName, remoteIp);

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<IHost> StartHostAsync()
    {
        var jobService = new Mock<IJobService>();
        jobService.SetupGet(j => j.Jobs).Returns(new List<KubernetesJob>
        {
            new("batch-nightly-slimfaas-job-1", JobStatus.Running, [JobIp], [], "element-1", 0, 0)
        });

        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<HistoryHttpMemoryService>();
                        services.AddSingleton<ISendClient, SendClientMock>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService, AllowedCallersReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(_ => new Mock<IWakeUpFunction>().Object);
                        services.AddSingleton(jobService.Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton<IWebSocketSendClient, WebSocketSendClientMock>();
                        services.AddSingleton<IClusterFileSync>(_ => new Mock<IClusterFileSync>().Object);
                        services.AddSingleton<IDatabaseService>(_ => new Mock<IDatabaseService>().Object);
                        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
                        {
                            Namespace = "default"
                        }));
                        services.AddMemoryCache();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<WakeUpGate>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        TestRemoteIp.Use(app);
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
            })
            .StartAsync();
    }

    private sealed class AllowedCallersReplicasService : IReplicasService
    {
        public DeploymentsInformations Deployments =>
            new(
                new List<DeploymentInformation>
                {
                    Function("payments", "10.0.1.1", FunctionVisibility.Private,
                        pathsStartWithVisibility: [new PathVisibility("health", FunctionVisibility.Public)],
                        allowedCallers: ["frontend", "batch-*"]),
                    Function("ledger", "10.0.1.2", FunctionVisibility.Private),
                    Function("frontend", FrontendIp),
                    Function("backoffice", BackofficeIp),
                    Function("batch-import", BatchImportIp),
                    Function("batch-untrusted", UntrustedIp, trust: FunctionTrust.Untrusted)
                },
                new SlimFaasDeploymentInformation(1, []),
                new List<PodInformation>());

        private static DeploymentInformation Function(string name, string podIp,
            FunctionVisibility visibility = FunctionVisibility.Public,
            IList<PathVisibility>? pathsStartWithVisibility = null,
            IList<string>? allowedCallers = null,
            FunctionTrust trust = FunctionTrust.Trusted)
            => new(name, "default",
                [new PodInformation($"{name}-0", true, true, podIp, name, [8080])],
                new SlimFaasConfiguration(), 1,
                Visibility: visibility,
                PathsStartWithVisibility: pathsStartWithVisibility,
                EndpointReady: true,
                Trust: trust,
                AllowedCallers: allowedCallers);

        public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();

        public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();

        public Task SyncDeploymentsFromSlimData(DeploymentsInformations deploymentsInformations) => Task.CompletedTask;
    }
}
