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
using SlimFaas.Security;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Endpoints;

public class EventEndpointTests
{
    [Theory]
    [InlineData("/publish-event/toto/hello", HttpStatusCode.NotFound, null)]
    [InlineData("/publish-event/reload/hello", HttpStatusCode.NoContent, "http://fibonacci-2.fibonacci:8080/hello,http://fibonacci-1.fibonacci:8080/hello")]
    [InlineData("/publish-event/reloadnoprefix/hello", HttpStatusCode.NoContent, "http://fibonacci-2.fibonacci:8080/hello,http://fibonacci-1.fibonacci:8080/hello")]
    [InlineData("/publish-event/wrong/download", HttpStatusCode.NotFound, null)]
    [InlineData("/publish-event/reloadprivate/hello", HttpStatusCode.NotFound, null)]
    public async Task CallPublishInSyncModeAndReturnOk(string path, HttpStatusCode expected, string? times)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        var sendClientMock = new SendClientMock();
        Mock<IJobService> jobServiceMock = new();

        jobServiceMock
            .Setup(k => k.SyncJobsAsync())
            .ReturnsAsync(new List<KubernetesJob>());
        jobServiceMock.Setup(k => k.Jobs).Returns(new List<KubernetesJob>());
        Environment.SetEnvironmentVariable(EnvironmentVariables.BaseFunctionPodUrl, "http://{pod_name}.{function_name}:8080/");

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        services.AddSingleton<ISendClient>(sendClientMock);
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService, MemoryReplicas2ReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(_ => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(_ => jobServiceMock.Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().PostAsync($"http://localhost:5000{path}", new StringContent(""));

        if (times != null)
        {
            var timesList = times.Split(",");
            Assert.Equal(timesList.Length, sendClientMock.SendDatas.Count);
            foreach (var time in sendClientMock.SendDatas)
            {
                var p = time.BaseUrl + time.Path;
                Assert.Contains(timesList, t => t == p);
            }
        }

        Assert.Equal(expected, response.StatusCode);
    }
}
