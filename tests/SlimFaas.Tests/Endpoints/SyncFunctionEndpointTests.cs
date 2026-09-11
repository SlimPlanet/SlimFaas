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
using SlimFaas.Local;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.Tests.Endpoints;
using SlimFaas.WebSocket;
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
                It.IsAny<SlimFaasDefaultConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<Proxy?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
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
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton<IWebSocketSendClient, WebSocketSendClientMock>();
                        services.AddMemoryCache();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<WakeUpGate>();
                        services.AddSingleton<NetworkActivityTracker>();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallFunctionInSyncMode_FromJob_RecordsJobActivity(bool localGateway)
    {
        const string jobRunName = "daily-report-slimfaas-job-a1";
        var tracker = new NetworkActivityTracker();
        var sender = new Mock<ISendClient>();
        sender.Setup(s => s.SendHttpRequestSync(It.IsAny<Microsoft.AspNetCore.Http.HttpContext>(),
            "fibonacci", "compute", "", It.IsAny<SlimFaasSyncConfiguration>(), null,
            It.IsAny<IProxy>(), NetworkActivityTracker.Actors.SlimFaas, jobRunName, activityCorrelationId: It.IsAny<string?>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.SetupGet(service => service.Jobs).Returns(
        [
            new KubernetesJob(
                jobRunName,
                JobStatus.Running,
                ["10.42.0.17"],
                [],
                "element-1",
                0,
                0)
        ]);

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<HistoryHttpMemoryService>();
                        services.AddSingleton(sender.Object);
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService, MemoryReplicas2ReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(_ => new Mock<IWakeUpFunction>().Object);
                        services.AddSingleton<IJobService>(_ => jobServiceMock.Object);
                        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
                            new SlimFaasOptions
                            {
                                Process = new ProcessOrchestratorOptions
                                {
                                    Token = "test-token"
                                }
                            }));
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton<IWebSocketSendClient, WebSocketSendClientMock>();
                        services.AddMemoryCache();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<WakeUpGate>();
                        services.AddSingleton(tracker);
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
            })
            .StartAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://localhost:5000/function/fibonacci/compute");
        if (localGateway)
        {
            request.Headers.TryAddWithoutValidation(LocalWorkloadGateway.JobHeaderName, jobRunName);
            request.Headers.TryAddWithoutValidation(LocalWorkloadGateway.SignatureHeaderName,
                LocalWorkloadGateway.CreateSignature(jobRunName, "test-token"));
        }
        else request.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.42.0.17");

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.VerifyAll();
        Assert.Equal(tracker.GetRecent()[0].Id, sender.Invocations.Single().Arguments[9]);
        var events = tracker.GetRecent();
        Assert.Equal(2, events.Count);
        Assert.Equal(NetworkActivityTracker.EventTypes.RequestIn, events[0].Type);
        Assert.Equal("daily-report", events[0].Source);
        Assert.Equal(jobRunName, events[0].SourcePod);
        Assert.Equal(NetworkActivityTracker.EventTypes.RequestEnd, events[1].Type);
        Assert.Equal(events[0].Id, events[1].CorrelationId);
        Assert.Equal("daily-report", events[1].Source);
        Assert.Equal(jobRunName, events[1].SourcePod);
    }
}
