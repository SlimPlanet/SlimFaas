using System.Net;
using System.Text.Json;
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
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public class JobStatusEndpointTests
{
    [Fact(DisplayName = "GET /jobs/status returns 200 with job configurations")]
    public async Task GetJobStatus_Returns200_WithConfigurations()
    {
        // Arrange
        var jobConfigMock = new Mock<IJobConfiguration>();
        var jobServiceMock = new Mock<IJobService>();
        var scheduleJobServiceMock = new Mock<IScheduleJobService>();
        var databaseServiceMock = new Mock<IDatabaseService>();

        var publicJob = new SlimfaasJob(
            Image: "my-image:latest",
            ImagesWhitelist: new() { "my-image:latest" },
            Visibility: nameof(FunctionVisibility.Public),
            NumberParallelJob: 3,
            Resources: new CreateJobResources(
                new Dictionary<string, string> { { "cpu", "100m" }, { "memory", "128Mi" } },
                new Dictionary<string, string> { { "cpu", "200m" }, { "memory", "256Mi" } }));

        var config = new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>
            {
                { "test-job", publicJob }
            },
            new Dictionary<string, IList<ScheduleCreateJob>>
            {
                {
                    "test-job", new List<ScheduleCreateJob>
                    {
                        new("*/5 * * * *", new List<string> { "arg1" }, "my-image:latest")
                    }
                }
            });

        jobConfigMock.SetupGet(c => c.Configuration).Returns(config);

        var runningJobs = new List<SlimFaas.Kubernetes.Job>
        {
            new("test-job-slimfaas-job-abc123",
                SlimFaas.Kubernetes.JobStatus.Running,
                new List<string> { "10.0.0.1" },
                new List<string>(),
                "elem-1",
                DateTime.UtcNow.Ticks,
                DateTime.UtcNow.Ticks)
        };
        jobServiceMock.SetupGet(j => j.Jobs).Returns(runningJobs);
        scheduleJobServiceMock.Setup(s => s.ListScheduleJobAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ListScheduleJob>());

        using IHost host = await new HostBuilder()
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
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(new Mock<IWakeUpFunction>().Object);
                        services.AddSingleton<IJobConfiguration>(jobConfigMock.Object);
                        services.AddSingleton<IJobService>(jobServiceMock.Object);
                        services.AddSingleton<IScheduleJobService>(scheduleJobServiceMock.Object);
                        services.AddSingleton<IDatabaseService>(databaseServiceMock.Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
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

        // Act
        var response = await host.GetTestClient().GetAsync("http://localhost:5000/jobs/status");
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = JsonSerializer.Deserialize<List<JobConfigurationStatus>>(body,
            ListJobConfigurationStatusSerializerContext.Default.ListJobConfigurationStatus);

        Assert.NotNull(result);
        Assert.Single(result);

        var jobStatus = result[0];
        Assert.Equal("test-job", jobStatus.Name);
        Assert.Equal("Public", jobStatus.Visibility);
        Assert.Equal("my-image:latest", jobStatus.Image);
        Assert.Equal(3, jobStatus.NumberParallelJob);
        Assert.NotNull(jobStatus.Resources);

        // Should have one Kubernetes schedule
        Assert.Single(jobStatus.Schedules);
        Assert.Equal("*/5 * * * *", jobStatus.Schedules[0].Schedule);
        Assert.NotNull(jobStatus.Schedules[0].NextExecutionTimestamp);
        Assert.True(jobStatus.Schedules[0].NextExecutionTimestamp > 0);

        // Should have one running job
        Assert.Single(jobStatus.RunningJobs);
        Assert.Equal("Running", jobStatus.RunningJobs[0].Status);
    }

    [Fact(DisplayName = "GET /jobs/status returns empty list when no configurations")]
    public async Task GetJobStatus_ReturnsEmptyList_WhenNoConfigurations()
    {
        var jobConfigMock = new Mock<IJobConfiguration>();
        var jobServiceMock = new Mock<IJobService>();
        var scheduleJobServiceMock = new Mock<IScheduleJobService>();
        var databaseServiceMock = new Mock<IDatabaseService>();

        jobConfigMock.SetupGet(c => c.Configuration)
            .Returns(new SlimFaasJobConfiguration(new Dictionary<string, SlimfaasJob>()));
        jobServiceMock.SetupGet(j => j.Jobs).Returns(new List<SlimFaas.Kubernetes.Job>());

        using IHost host = await new HostBuilder()
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
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(new Mock<IWakeUpFunction>().Object);
                        services.AddSingleton<IJobConfiguration>(jobConfigMock.Object);
                        services.AddSingleton<IJobService>(jobServiceMock.Object);
                        services.AddSingleton<IScheduleJobService>(scheduleJobServiceMock.Object);
                        services.AddSingleton<IDatabaseService>(databaseServiceMock.Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
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

        var response = await host.GetTestClient().GetAsync("http://localhost:5000/jobs/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", body);
    }

    [Fact(DisplayName = "GET /jobs/status includes API-configured schedules")]
    public async Task GetJobStatus_IncludesApiSchedules()
    {
        var jobConfigMock = new Mock<IJobConfiguration>();
        var jobServiceMock = new Mock<IJobService>();
        var scheduleJobServiceMock = new Mock<IScheduleJobService>();
        var databaseServiceMock = new Mock<IDatabaseService>();

        var job = new SlimfaasJob(
            Image: "img:v1",
            ImagesWhitelist: new() { "img:v1" },
            Visibility: nameof(FunctionVisibility.Private));

        jobConfigMock.SetupGet(c => c.Configuration)
            .Returns(new SlimFaasJobConfiguration(
                new Dictionary<string, SlimfaasJob> { { "my-job", job } }));
        jobServiceMock.SetupGet(j => j.Jobs).Returns(new List<SlimFaas.Kubernetes.Job>());

        // API schedule
        scheduleJobServiceMock.Setup(s => s.ListScheduleJobAsync("my-job"))
            .ReturnsAsync(new List<ListScheduleJob>
            {
                new("sched-1", "0 */2 * * *", new List<string> { "run" }, "img:v1")
            });

        using IHost host = await new HostBuilder()
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
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(new Mock<IWakeUpFunction>().Object);
                        services.AddSingleton<IJobConfiguration>(jobConfigMock.Object);
                        services.AddSingleton<IJobService>(jobServiceMock.Object);
                        services.AddSingleton<IScheduleJobService>(scheduleJobServiceMock.Object);
                        services.AddSingleton<IDatabaseService>(databaseServiceMock.Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
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

        var response = await host.GetTestClient().GetAsync("http://localhost:5000/jobs/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = JsonSerializer.Deserialize<List<JobConfigurationStatus>>(body,
            ListJobConfigurationStatusSerializerContext.Default.ListJobConfigurationStatus);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("my-job", result[0].Name);
        Assert.Equal("Private", result[0].Visibility);
        Assert.Single(result[0].Schedules);
        Assert.Equal("0 */2 * * *", result[0].Schedules[0].Schedule);
        Assert.NotNull(result[0].Schedules[0].NextExecutionTimestamp);
    }
}



