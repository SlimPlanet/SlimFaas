using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Security;
using SlimFaas.Tests.Endpoints;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Endpoints;

public class SyncFunctionEndpointTests
{
    [Theory]
    [InlineData("/function/fibonacci/compute", HttpStatusCode.OK)]
    [InlineData("/function/fibonacci/noprefix", HttpStatusCode.OK)]
    [InlineData("/function/fibonacci/download", HttpStatusCode.OK)]
    [InlineData("/function/wrong/download", HttpStatusCode.NotFound)]
    [InlineData("/function/fibonacci/private", HttpStatusCode.NotFound)]
    public async Task CallFunctionInSyncModeAndReturnOk(string path, HttpStatusCode expected)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        HttpResponseMessage responseMessage = new();
        responseMessage.StatusCode = HttpStatusCode.OK;
        Mock<ISendClient> sendClientMock = new Mock<ISendClient>();
        sendClientMock.Setup(s => s.SendHttpRequestAsync(It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<Proxy?>()))
            .ReturnsAsync(responseMessage);

        Mock<IJobService> jobServiceMock = new();
        jobServiceMock
            .Setup(k => k.SyncJobsAsync())
            .ReturnsAsync(new List<KubernetesJob>());
        jobServiceMock.Setup(k => k.Jobs).Returns(new List<KubernetesJob>());

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        services.AddSingleton<ISendClient, SendClientMock>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService, MemoryReplicas2ReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(_ => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(_ => jobServiceMock.Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddMemoryCache();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<WakeUpGate>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

        Assert.Equal(expected, response.StatusCode);
    }
}
