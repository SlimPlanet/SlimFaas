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

public class WakeFunctionEndpointTests
{
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
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
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

        HttpResponseMessage response = await host.GetTestClient().PostAsync($"http://localhost:5000{path}", new StringContent(""));

        wakeUpFunctionMock.Verify(k => k.FireAndForgetWakeUpAsync(It.IsAny<string>()), Times.AtMost(numberFireAndForgetWakeUpAsyncCall));
        Assert.Equal(expectedHttpStatusCode, response.StatusCode);
    }
}

