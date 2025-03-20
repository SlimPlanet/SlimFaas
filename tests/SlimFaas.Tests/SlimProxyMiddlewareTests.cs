using System.Net;
using System.Net.Http.Json;
using DotNext.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimFaas.Kubernetes;
using MemoryPack;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Jobs;

namespace SlimFaas.Tests;

internal class MemoryReplicasService : IReplicasService
{
    public DeploymentsInformations Deployments =>
        new(
            new List<DeploymentInformation>
            {
                new(Replicas: 0, Deployment: "fibonacci", Namespace: "default",
                    Pods: new List<PodInformation> { new("", true, true, "", "") }, Configuration: new SlimFaasConfiguration())
            }, new SlimFaasDeploymentInformation(1, new List<PodInformation>()), new List<PodInformation>());

    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();

    public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();

    public async Task SyncDeploymentsFromSlimData(DeploymentsInformations deploymentsInformations)
    {
        await Task.Delay(100);
    }
}

internal class MemoryReplicas2ReplicasService : IReplicasService
{
    public DeploymentsInformations Deployments =>
        new(
            new List<DeploymentInformation>
            {
                new(Replicas: 2,
                    Deployment: "fibonacci",
                    SubscribeEvents: new List<string>() {
                        "Public:reload",
                        "Private:reloadprivate",
                        "reloadnoprefix"
                    },
                    PathsStartWithVisibility: new List<string>()
                    {
                        "Public:/compute",
                        "Private:/private",
                        "/noprefix",
                    },
                    Namespace: "default",
                    Configuration: new SlimFaasConfiguration(),
                    Pods: new List<PodInformation> {
                        new("fibonacci-1", true, true, "0", "fibonacci"),
                        new("fibonacci-2", true, true, "0", "fibonacci"),
                        new("fibonacci-3", false, false, "0", "fibonacci")
                    })
            }, new SlimFaasDeploymentInformation(1, new List<PodInformation>()), new List<PodInformation>());

    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();

    public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();

    public async Task SyncDeploymentsFromSlimData(DeploymentsInformations deploymentsInformations)
    {
        await Task.Delay(100);
    }
}

internal class MemorySlimFaasQueue : ISlimFaasQueue
{
    public Task<IList<QueueData>?> DequeueAsync(string key, int count = 1) => throw new NotImplementedException();

    public Task<long> CountElementAsync(string key, IList<CountType> countTypes, int maximum) => throw new NotImplementedException();

    public Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus) => throw new NotImplementedException();

    public async Task EnqueueAsync(string key, byte[] message, RetryInformation retryInformation) => await Task.Delay(100);

}

internal record SendData(string FunctionName, string Path, string BaseUrl);

internal class SendClientMock : ISendClient
{
    public IList<SendData> SendDatas = new List<SendData>();
    public Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, Proxy proxy=null)
    {
        HttpResponseMessage responseMessage = new HttpResponseMessage();
        responseMessage.StatusCode = HttpStatusCode.OK;
        Task.Delay(100).Wait();
        SendDatas.Add(new(customRequest.FunctionName, customRequest.Path, baseUrl));
        return Task.FromResult(responseMessage);
    }

    public Task<HttpResponseMessage> SendHttpRequestSync(HttpContext httpContext, string functionName,
        string functionPath, string functionQuery, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, Proxy proxy = null)
    {
        HttpResponseMessage responseMessage = new HttpResponseMessage();
        responseMessage.StatusCode = HttpStatusCode.OK;
        Task.Delay(100).Wait();
        SendDatas.Add(new(functionName, functionPath, baseUrl));
        return Task.FromResult(responseMessage);
    }
}

public class ProxyMiddlewareTests
{

    [Theory]
    [InlineData("/publish-event/toto/hello", HttpStatusCode.NoContent, "http://localhost:5002/hello" )]
    [InlineData("/publish-event/reload/hello", HttpStatusCode.NoContent, "http://fibonacci-2.fibonacci:8080//hello,http://fibonacci-1.fibonacci:8080//hello,http://localhost:5002/hello" )]
    [InlineData("/publish-event/reloadnoprefix/hello", HttpStatusCode.NoContent,  "http://fibonacci-2.fibonacci:8080//hello,http://fibonacci-1.fibonacci:8080//hello")]
    [InlineData("/publish-event/wrong/download", HttpStatusCode.NotFound, null)]
    [InlineData("/publish-event/reloadprivate/hello", HttpStatusCode.NotFound, null)]
    public async Task CallPublishInSyncModeAndReturnOk(string path, HttpStatusCode expected, string? times)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        HttpResponseMessage responseMessage = new HttpResponseMessage();
        responseMessage.StatusCode = HttpStatusCode.OK;
        var sendClientMock = new SendClientMock();
        Mock<IJobService> jobServiceMock = new();

        jobServiceMock
            .Setup(k => k.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());
        jobServiceMock.Setup(k => k.Jobs).Returns(new List<Job>());

        System.Environment.SetEnvironmentVariable(EnvironmentVariables.SlimFaasSubscribeEvents,
            "reload=>http://localhost:5002,toto=>http://localhost:5002");

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        services.AddSingleton<ISendClient, ISendClient>(sc => sendClientMock);
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<IReplicasService, MemoryReplicas2ReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                    })
                    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

        if (times != null)
        {
            var functionPath = $"/{path.Split("/")[3]}";
            var timesList = times.Split(",");
            Assert.Equal(timesList.Length, sendClientMock.SendDatas.Count);
            foreach (var time in sendClientMock.SendDatas)
            {
                var p = time.BaseUrl + time.Path;
                Assert.Contains(p, timesList);
            }
        }

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("/function/fibonacci/compute", HttpStatusCode.OK)]
    [InlineData("/function/fibonacci/noprefix", HttpStatusCode.OK)]
    [InlineData("/function/fibonacci/download", HttpStatusCode.OK)]
    [InlineData("/function/wrong/download", HttpStatusCode.NotFound)]
    [InlineData("/function/fibonacci/private", HttpStatusCode.NotFound)]
    public async Task CallFunctionInSyncModeAndReturnOk(string path, HttpStatusCode expected)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        HttpResponseMessage responseMessage = new HttpResponseMessage();
        responseMessage.StatusCode = HttpStatusCode.OK;
        Mock<ISendClient> sendClientMock = new Mock<ISendClient>();
        sendClientMock.Setup(s => s.SendHttpRequestAsync(It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<Proxy?>()))
            .ReturnsAsync(responseMessage);

        Mock<IJobService> jobServiceMock = new();
        jobServiceMock
            .Setup(k => k.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());
        jobServiceMock.Setup(k => k.Jobs).Returns(new List<Job>());

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
                        services.AddSingleton<IReplicasService, MemoryReplicas2ReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                    })
                    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("/async-function/fibonacci/download", HttpStatusCode.Accepted)]
    [InlineData("/async-function/wrong/download", HttpStatusCode.NotFound)]
    public async Task CallFunctionInAsyncSyncModeAndReturnOk(string path, HttpStatusCode expected)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        Mock<IJobService> jobServiceMock = new();
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
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                    })
                    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("/wake-function/fibonacci", HttpStatusCode.NoContent, 1)]
    [InlineData("/wake-function/wrong", HttpStatusCode.NotFound, 0)]
    public async Task JustWakeFunctionAndReturnOk(string path, HttpStatusCode expectedHttpStatusCode,
        int numberFireAndForgetWakeUpAsyncCall)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        Mock<IJobService> jobServiceMock = new();
        wakeUpFunctionMock.Setup(k => k.FireAndForgetWakeUpAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
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
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                    })
                    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");
        HistoryHttpMemoryService historyHttpMemoryService =
            host.Services.GetRequiredService<HistoryHttpMemoryService>();

        wakeUpFunctionMock.Verify(k => k.FireAndForgetWakeUpAsync(It.IsAny<string>()), Times.AtMost(numberFireAndForgetWakeUpAsyncCall));
        Assert.Equal(expectedHttpStatusCode, response.StatusCode);
    }

    [Theory]
    [InlineData("/status-function/fibonacci", HttpStatusCode.OK, "{\"NumberReady\":1,\"NumberRequested\":0,\"PodType\":\"Deployment\",\"Visibility\":\"Public\",\"Name\":\"fibonacci\"}")]
    [InlineData("/status-function/wrong", HttpStatusCode.NotFound, "")]
    [InlineData("/status-functions", HttpStatusCode.OK, "[{\"NumberReady\":1,\"NumberRequested\":0,\"PodType\":\"Deployment\",\"Visibility\":\"Public\",\"Name\":\"fibonacci\"}]")]
    public async Task GetStatusFunctionAndReturnOk(string path, HttpStatusCode expectedHttpStatusCode,
        string expectedBody)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        Mock<IJobService> jobServiceMock = new();
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
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                    })
                    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expectedBody, body);
        Assert.Equal(expectedHttpStatusCode, response.StatusCode);
    }

    [Theory]
    [InlineData("/job/daisy", HttpStatusCode.NoContent, 1)]
    public async Task RunJobAndReturnOk(string path, HttpStatusCode expectedHttpStatusCode,
        int numberFireJob)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        Mock<IJobService> jobServiceMock = new();
        jobServiceMock.Setup(k => k.EnqueueJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>(), It.IsAny<bool>()))
            .ReturnsAsync(new EnqueueJobResult(""));
        jobServiceMock.Setup(k => k.Jobs).Returns(new List<Job>());
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
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                    })
                    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().PostAsync($"http://localhost:5000{path}",  JsonContent.Create(new CreateJob(new List<string>(),"youhou")));
        HistoryHttpMemoryService historyHttpMemoryService =
            host.Services.GetRequiredService<HistoryHttpMemoryService>();

        jobServiceMock.Verify(k => k.CreateJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>()), Times.AtMost(numberFireJob));
        Assert.Equal(expectedHttpStatusCode, response.StatusCode);
    }
}
