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

namespace SlimFaas.Tests.Endpoints;

public class StatusFunctionEndpointTests
{
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
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
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

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expectedBody, body);
        Assert.Equal(expectedHttpStatusCode, response.StatusCode);
    }
}
