using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Jobs;

public class JobEndpointsTests
{
    #region Infrastructure commune

    private static async Task<(IHost host,
            Mock<IJobService> jobServiceMock,
            Mock<IWakeUpFunction> wakeUpFunctionMock)>
        BuildHostAsync(Action<Mock<IJobService>> setupJobService)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        Mock<IJobService> jobServiceMock = new();
        setupJobService(jobServiceMock); // spécifique à chaque test

        IHost host = await new HostBuilder()
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        s.AddSingleton<ISendClient, SendClientMock>();
                        s.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        s.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        s.AddSingleton<IReplicasService, MemoryReplicasService>();
                        s.AddSingleton<IWakeUpFunction>(_ => wakeUpFunctionMock.Object);
                        s.AddSingleton<IJobService>(_ => jobServiceMock.Object);
                    })
                    .Configure(app => app.UseMiddleware<SlimProxyMiddleware>());
            })
            .StartAsync();

        return (host, jobServiceMock, wakeUpFunctionMock);
    }

    #endregion

    #region POST /job/{name}

    [Theory]
    [InlineData("/job/daisy", HttpStatusCode.BadRequest, 1, "")]
    public async Task RunJob_Returns_expected_status(string path,
        HttpStatusCode expectedStatus,
        int expectedCreateCalls,
        string expectedBody)
    {
        (IHost host, Mock<IJobService> jobSvc, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.Setup(s => s.EnqueueJobAsync(
                    It.IsAny<string>(),
                    It.IsAny<CreateJob>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new ResultWithError<EnqueueJobResult>(null, new ErrorResult("key")));
            jobServiceMock.SetupGet(s => s.Jobs)
                .Returns(new List<Job>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync($"http://localhost:5000{path}",
                JsonContent.Create(new CreateJob(new List<string>(), "youhou")));

        Assert.Equal(expectedStatus, resp.StatusCode);
        Assert.Equal(expectedBody, await resp.Content.ReadAsStringAsync());
        jobSvc.Verify(s => s.CreateJobAsync(It.IsAny<string>(),
                It.IsAny<CreateJob>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>()),
            Times.AtMost(expectedCreateCalls));
    }

    #endregion

    #region GET /job/{name}

    [Theory]
    [InlineData("/job/daisy",
        "[{\"Name\":\"1\",\"Status\":\"daisy-slimfaas-job-12772\",\"Id\":\"queued\",\"PositionInQueue\":1,\"InQueueTimestamp\":0,\"StartTimestamp\":0},{\"Name\":\"2\",\"Status\":\"daisy-slimfaas-job-12732\",\"Id\":\"running\",\"PositionInQueue\":-1,\"InQueueTimestamp\":0,\"StartTimestamp\":0}]")]
    public async Task ListJobs_Returns_queue_snapshot(string path, string expectedJson)
    {
        List<JobListResult> expected = new()
        {
            new JobListResult("1", "daisy-slimfaas-job-12772", "queued", 1),
            new JobListResult("2", "daisy-slimfaas-job-12732", "running")
        };

        (IHost host, Mock<IJobService> jobSvc, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.Setup(s => s.ListJobAsync("daisy"))
                .ReturnsAsync(expected);
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .GetAsync($"http://localhost:5000{path}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string json = await resp.Content.ReadAsStringAsync();
        Assert.Equal(expectedJson, json);
        jobSvc.Verify(s => s.ListJobAsync("daisy"), Times.Once);
    }

    #endregion

    #region DELETE /job/{id}

    [Theory]
    [InlineData("/job/daisy/1", true, HttpStatusCode.OK)]
    [InlineData("/job/daisy/999", false, HttpStatusCode.NotFound)]
    public async Task DeleteJob_Returns_expected_status(string path,
        bool deleteSucceeded,
        HttpStatusCode expectedStatus)
    {
        string jobId = path.Split('/').Last();

        (IHost host, Mock<IJobService> jobSvc, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.Setup(s => s.DeleteJobAsync("daisy", jobId, false))
                .ReturnsAsync(deleteSucceeded);
            jobServiceMock.Setup(s => s.Jobs).Returns(new List<Job>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .DeleteAsync($"http://localhost:5000{path}");

        Assert.Equal(expectedStatus, resp.StatusCode);
        jobSvc.Verify(s => s.DeleteJobAsync("daisy", jobId, false), Times.Once);
    }

    #endregion
}
