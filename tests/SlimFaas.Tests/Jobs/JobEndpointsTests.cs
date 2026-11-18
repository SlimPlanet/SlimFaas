using System.Net;
using System.Net.Http.Json;
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
using SlimFaas.WebSocket;
using KubernetesJob = SlimFaas.Kubernetes.Job;

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
        Mock<IJobConfiguration> jobConfigurationMock = new();
        jobConfigurationMock.SetupGet(c => c.Configuration).Returns(new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase)
            {
                ["daisy"] = new("image", []),
                [JobConfiguration.Default] = new("image", [])
            }));

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
                        s.AddSingleton<IJobConfiguration>(_ => jobConfigurationMock.Object);
                        s.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        s.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        s.AddMemoryCache();
                        s.AddSingleton<FunctionStatusCache>();
                        s.AddSingleton<WakeUpGate>();
                        s.AddSingleton<NetworkActivityTracker>();
                        s.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
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
                .Returns(new List<KubernetesJob>());
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

    [Theory(DisplayName = "POST /job/{name} – trace l'entrée de l'exécution dans la file du job")]
    [InlineData("/job/daisy", "daisy")]
    [InlineData("/job/unknown", JobConfiguration.Default)]
    public async Task CreateJob_Records_Request_And_Enqueue_Activity(string path, string configurationName)
    {
        (IHost host, _, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.Setup(s => s.EnqueueJobAsync(
                    It.IsAny<string>(),
                    It.IsAny<CreateJob>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new ResultWithError<EnqueueJobResult>(new EnqueueJobResult("element-1")));
            jobServiceMock.SetupGet(s => s.Jobs)
                .Returns(new List<KubernetesJob>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync($"http://localhost:5000{path}",
                JsonContent.Create(new CreateJob(new List<string>(), "image")));

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        IList<NetworkActivityEvent> activity = host.Services.GetRequiredService<NetworkActivityTracker>().GetRecent();
        Assert.Equal(2, activity.Count);
        NetworkActivityEvent requestIn = activity[0];
        Assert.Equal(NetworkActivityTracker.EventTypes.RequestIn, requestIn.Type);
        Assert.Equal(NetworkActivityTracker.Actors.External, requestIn.Source);
        Assert.Equal(NetworkActivityTracker.Actors.SlimFaas, requestIn.Target);
        NetworkActivityEvent enqueue = activity[1];
        Assert.Equal(NetworkActivityTracker.EventTypes.Enqueue, enqueue.Type);
        Assert.Equal(NetworkActivityTracker.Actors.SlimFaas, enqueue.Source);
        Assert.Equal(configurationName, enqueue.Target);
        Assert.Equal($"job:{configurationName}", enqueue.QueueName);
        Assert.Equal(requestIn.SourcePod, enqueue.SourcePod);
        Assert.Equal(requestIn.Id, enqueue.CorrelationId);
    }

    [Fact(DisplayName = "POST /job/{name} – une exécution refusée répond à l'appelant sans entrer dans la file")]
    public async Task CreateJob_Rejected_Records_Response_Without_Enqueue()
    {
        (IHost host, _, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.Setup(s => s.EnqueueJobAsync(
                    It.IsAny<string>(),
                    It.IsAny<CreateJob>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new ResultWithError<EnqueueJobResult>(null, new ErrorResult("image_not_allowed")));
            jobServiceMock.SetupGet(s => s.Jobs)
                .Returns(new List<KubernetesJob>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync("http://localhost:5000/job/daisy",
                JsonContent.Create(new CreateJob(new List<string>(), "image")));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        IList<NetworkActivityEvent> activity = host.Services.GetRequiredService<NetworkActivityTracker>().GetRecent();
        Assert.Equal(
            new[] { NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.EventTypes.RequestEnd },
            activity.Select(e => e.Type));
        Assert.Equal(activity[0].Id, activity[1].CorrelationId);
        Assert.Null(activity[1].QueueName);
    }

    #endregion

    #region POST /job/{name} - Validation du nom de fonction

    [Theory(DisplayName = "POST /job/{name} – retourne 400 si le nom de fonction est invalide")]
    [InlineData("/job/ab")]
    [InlineData("/job/abcdefghijklmnopqrstuvwxyz12345")]
    [InlineData("/job/test.func")]
    [InlineData("/job/test func")]
    [InlineData("/job/test@func")]
    public async Task CreateJob_Returns_400_When_FunctionName_Invalid(string path)
    {
        (IHost host, Mock<IJobService> jobSvc, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.SetupGet(s => s.Jobs)
                .Returns(new List<KubernetesJob>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync($"http://localhost:5000{path}",
                JsonContent.Create(new CreateJob(new List<string>(), "image")));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        jobSvc.Verify(s => s.EnqueueJobAsync(
                It.IsAny<string>(),
                It.IsAny<CreateJob>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Theory(DisplayName = "POST /job/{name} – accepte les noms valides")]
    [InlineData("/job/abc")]
    [InlineData("/job/abcdefghijkl")]
    [InlineData("/job/abcdefghijklmnopqrstuvwxyz1234")]
    [InlineData("/job/test-func")]
    [InlineData("/job/my-app")]
    [InlineData("/job/daisy")]
    [InlineData("/job/ab1")]
    [InlineData("/job/test_func")]
    [InlineData("/job/abc_123")]
    public async Task CreateJob_Accepts_Valid_FunctionNames(string path)
    {
        (IHost host, Mock<IJobService> jobSvc, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.Setup(s => s.EnqueueJobAsync(
                    It.IsAny<string>(),
                    It.IsAny<CreateJob>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new ResultWithError<EnqueueJobResult>(null, new ErrorResult("key")));
            jobServiceMock.SetupGet(s => s.Jobs)
                .Returns(new List<KubernetesJob>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync($"http://localhost:5000{path}",
                JsonContent.Create(new CreateJob(new List<string>(), "image")));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        jobSvc.Verify(s => s.EnqueueJobAsync(
                It.IsAny<string>(),
                It.IsAny<CreateJob>(),
                It.IsAny<bool>()),
            Times.Once);
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
            jobServiceMock.Setup(s => s.Jobs).Returns(new List<KubernetesJob>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .DeleteAsync($"http://localhost:5000{path}");

        Assert.Equal(expectedStatus, resp.StatusCode);
        jobSvc.Verify(s => s.DeleteJobAsync("daisy", jobId, false), Times.Once);
    }

    #endregion

    #region DELETE /job/{name}/{id} - Validation du nom de fonction

    [Theory(DisplayName = "DELETE /job/{name}/{id} – retourne 400 si le nom de fonction est invalide")]
    [InlineData("/job/ab/123")]
    [InlineData("/job/abcdefghijklmnopqrstuvwxyz12345/123")]
    public async Task DeleteJob_Returns_400_When_FunctionName_Invalid(string path)
    {
        (IHost host, Mock<IJobService> jobSvc, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.Setup(s => s.Jobs).Returns(new List<KubernetesJob>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .DeleteAsync($"http://localhost:5000{path}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        jobSvc.Verify(s => s.DeleteJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Theory(DisplayName = "DELETE /job/{name}/{id} – accepte les noms valides")]
    [InlineData("/job/abc/123")]
    [InlineData("/job/test-func/123")]
    [InlineData("/job/ab1/123")]
    [InlineData("/job/test_func/123")]
    public async Task DeleteJob_Accepts_Valid_FunctionNames(string path)
    {
        (IHost host, Mock<IJobService> jobSvc, _) = await BuildHostAsync(jobServiceMock =>
        {
            jobServiceMock.Setup(s => s.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), false))
                .ReturnsAsync(false);
            jobServiceMock.Setup(s => s.Jobs).Returns(new List<KubernetesJob>());
        });

        HttpResponseMessage resp = await host.GetTestClient()
            .DeleteAsync($"http://localhost:5000{path}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        jobSvc.Verify(s => s.DeleteJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()),
            Times.Once);
    }

    #endregion
}
