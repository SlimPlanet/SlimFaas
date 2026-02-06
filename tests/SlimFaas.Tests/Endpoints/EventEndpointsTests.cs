using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Endpoints;

public class EventEndpointsTests
{
    private static IOptions<SlimFaasOptions> CreateSlimFaasOptions()
    {
        return Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            Namespace = "default",
            BaseFunctionPodUrl = "http://{pod_ip}:{pod_port}",
            BaseSlimDataUrl = "http://{pod_name}.{service_name}.{namespace}.svc:3262"
        });
    }

    private static INamespaceProvider CreateNamespaceProvider()
    {
        var mock = new Mock<INamespaceProvider>();
        mock.SetupGet(n => n.CurrentNamespace).Returns("default");
        return mock.Object;
    }

    private static DeploymentsInformations CreateTestDeployments()
    {
        var function = new DeploymentInformation(
            Replicas: 1,
            Deployment: "test-function",
            SubscribeEvents: new List<SubscribeEvent>
            {
                new SubscribeEvent("test-event", FunctionVisibility.Public)
            },
            PathsStartWithVisibility: new List<PathVisibility>(),
            Namespace: "default",
            Configuration: new SlimFaasConfiguration
            {
                DefaultPublish = new SlimFaasDefaultConfiguration
                {
                    HttpTimeout = 30
                }
            },
            Pods: new List<PodInformation>
            {
                new PodInformation("test-pod-0", true, true, "10.0.0.1", "test-function", new List<int> { 8080 })
            },
            EndpointReady: true
        );

        return new DeploymentsInformations(
            new List<DeploymentInformation> { function },
            new SlimFaasDeploymentInformation(1, new List<PodInformation>
            {
                new("slimfaas-pod", true, true, "10.0.0.100", "slimfaas", new List<int> { 5000 })
            }),
            new List<PodInformation>()
        );
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task PublishEvent_AllHttpMethods_ShouldBeAccepted(string httpMethod)
    {
        // Arrange
        var deployments = CreateTestDeployments();
        var replicasServiceMock = new Mock<IReplicasService>();
        replicasServiceMock.Setup(r => r.Deployments).Returns(deployments);

        var sendClientMock = new Mock<ISendClient>();
        sendClientMock
            .Setup(s => s.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<Proxy?>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.Setup(j => j.Jobs).Returns(new List<KubernetesJob>());

        var accessPolicyMock = new Mock<IFunctionAccessPolicy>();
        accessPolicyMock
            .Setup(a => a.GetAllowedSubscribers(It.IsAny<HttpContext>(), "test-event"))
            .Returns(new List<DeploymentInformation> { deployments.Functions[0] });

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<HistoryHttpMemoryService>();
                        s.AddSingleton(sendClientMock.Object);
                        s.AddSingleton(replicasServiceMock.Object);
                        s.AddSingleton(jobServiceMock.Object);
                        s.AddSingleton(accessPolicyMock.Object);
                        s.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        s.AddSingleton(CreateSlimFaasOptions());
                        s.AddSingleton(CreateNamespaceProvider());
                        s.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapEventEndpoints());
                    });
            })
            .StartAsync();

        // Act
        var client = host.GetTestClient();
        var request = new HttpRequestMessage(new HttpMethod(httpMethod), "http://localhost:5000/publish-event/test-event");
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        sendClientMock.Verify(s => s.SendHttpRequestAsync(
            It.IsAny<CustomRequest>(),
            It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationTokenSource?>(),
            It.IsAny<Proxy?>()), Times.Once);
    }

    [Theory]
    [InlineData("GET", "/path")]
    [InlineData("POST", "/path")]
    [InlineData("PUT", "/api/resource")]
    [InlineData("DELETE", "/api/resource/123")]
    [InlineData("PATCH", "/update")]
    public async Task PublishEvent_WithFunctionPath_AllHttpMethods_ShouldBeAccepted(string httpMethod, string functionPath)
    {
        // Arrange
        var deployments = CreateTestDeployments();
        var replicasServiceMock = new Mock<IReplicasService>();
        replicasServiceMock.Setup(r => r.Deployments).Returns(deployments);

        var sendClientMock = new Mock<ISendClient>();
        sendClientMock
            .Setup(s => s.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<Proxy?>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.Setup(j => j.Jobs).Returns(new List<KubernetesJob>());

        var accessPolicyMock = new Mock<IFunctionAccessPolicy>();
        accessPolicyMock
            .Setup(a => a.GetAllowedSubscribers(It.IsAny<HttpContext>(), "test-event"))
            .Returns(new List<DeploymentInformation> { deployments.Functions[0] });

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<HistoryHttpMemoryService>();
                        s.AddSingleton(sendClientMock.Object);
                        s.AddSingleton(replicasServiceMock.Object);
                        s.AddSingleton(jobServiceMock.Object);
                        s.AddSingleton(accessPolicyMock.Object);
                        s.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        s.AddSingleton(CreateSlimFaasOptions());
                        s.AddSingleton(CreateNamespaceProvider());
                        s.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapEventEndpoints());
                    });
            })
            .StartAsync();

        // Act
        var client = host.GetTestClient();
        var request = new HttpRequestMessage(new HttpMethod(httpMethod), $"http://localhost:5000/publish-event/test-event{functionPath}");
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task PublishEvent_NoSubscribers_ShouldReturnNotFound()
    {
        // Arrange
        var deployments = CreateTestDeployments();
        var replicasServiceMock = new Mock<IReplicasService>();
        replicasServiceMock.Setup(r => r.Deployments).Returns(deployments);

        var sendClientMock = new Mock<ISendClient>();
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.Setup(j => j.Jobs).Returns(new List<KubernetesJob>());

        var accessPolicyMock = new Mock<IFunctionAccessPolicy>();
        accessPolicyMock
            .Setup(a => a.GetAllowedSubscribers(It.IsAny<HttpContext>(), "non-existing-event"))
            .Returns(new List<DeploymentInformation>());

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<HistoryHttpMemoryService>();
                        s.AddSingleton(sendClientMock.Object);
                        s.AddSingleton(replicasServiceMock.Object);
                        s.AddSingleton(jobServiceMock.Object);
                        s.AddSingleton(accessPolicyMock.Object);
                        s.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        s.AddSingleton(CreateSlimFaasOptions());
                        s.AddSingleton(CreateNamespaceProvider());
                        s.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapEventEndpoints());
                    });
            })
            .StartAsync();

        // Act
        var client = host.GetTestClient();
        var response = await client.PostAsync("http://localhost:5000/publish-event/non-existing-event", new StringContent(""));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublishEvent_MultiplePods_ShouldSendToAllReadyPods()
    {
        // Arrange
        var function = new DeploymentInformation(
            Replicas: 3,
            Deployment: "test-function",
            SubscribeEvents: new List<SubscribeEvent>
            {
                new SubscribeEvent("test-event", FunctionVisibility.Public)
            },
            PathsStartWithVisibility: new List<PathVisibility>(),
            Namespace: "default",
            Configuration: new SlimFaasConfiguration
            {
                DefaultPublish = new SlimFaasDefaultConfiguration { HttpTimeout = 30 }
            },
            Pods: new List<PodInformation>
            {
                new PodInformation("test-pod-0", true, true, "10.0.0.1", "test-function", new List<int> { 8080 }),
                new PodInformation("test-pod-1", true, true, "10.0.0.2", "test-function", new List<int> { 8080 }),
                new PodInformation("test-pod-2", false, false, "10.0.0.3", "test-function", new List<int> { 8080 }) // Not ready
            },
            EndpointReady: true
        );

        var deployments = new DeploymentsInformations(
            new List<DeploymentInformation> { function },
            new SlimFaasDeploymentInformation(1, new List<PodInformation>
            {
                new("slimfaas-pod", true, true, "10.0.0.100", "slimfaas", new List<int> { 5000 })
            }),
            new List<PodInformation>()
        );

        var replicasServiceMock = new Mock<IReplicasService>();
        replicasServiceMock.Setup(r => r.Deployments).Returns(deployments);

        var sendClientMock = new Mock<ISendClient>();
        sendClientMock
            .Setup(s => s.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<Proxy?>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.Setup(j => j.Jobs).Returns(new List<KubernetesJob>());

        var accessPolicyMock = new Mock<IFunctionAccessPolicy>();
        accessPolicyMock
            .Setup(a => a.GetAllowedSubscribers(It.IsAny<HttpContext>(), "test-event"))
            .Returns(new List<DeploymentInformation> { function });

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<HistoryHttpMemoryService>();
                        s.AddSingleton(sendClientMock.Object);
                        s.AddSingleton(replicasServiceMock.Object);
                        s.AddSingleton(jobServiceMock.Object);
                        s.AddSingleton(accessPolicyMock.Object);
                        s.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        s.AddSingleton(CreateSlimFaasOptions());
                        s.AddSingleton(CreateNamespaceProvider());
                        s.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapEventEndpoints());
                    });
            })
            .StartAsync();

        // Act
        var client = host.GetTestClient();
        var response = await client.PostAsync("http://localhost:5000/publish-event/test-event", new StringContent(""));

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // Should be called 2 times (only for ready pods)
        sendClientMock.Verify(s => s.SendHttpRequestAsync(
            It.IsAny<CustomRequest>(),
            It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationTokenSource?>(),
            It.IsAny<Proxy?>()), Times.Exactly(2));
    }
}

