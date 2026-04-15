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

public class JobScheduleEndpointsTests
{
    #region Infrastructure commune -------------------------------------------------------------

    private static async Task<(IHost host,
        Mock<IScheduleJobService> schedSvc,
        Mock<IJobService> jobSvc)>
        BuildHostAsync(Action<Mock<IScheduleJobService>> setupScheduleSvc)
    {
        Mock<IScheduleJobService> scheduleSvcMock = new();
        Mock<IJobService>         jobServiceMock  = new();          // toujours requis par le middleware
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        setupScheduleSvc(scheduleSvcMock);
        jobServiceMock.SetupGet(s => s.Jobs)
            .Returns(new List<KubernetesJob>());

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

                        // Middlewares : mocks
                        s.AddSingleton<IScheduleJobService>(_ => scheduleSvcMock.Object);
                        s.AddSingleton<IJobService>(_ => jobServiceMock.Object);

                        // Les dépendances inutilisées dans ces scénarios peuvent être omises
                        s.AddSingleton<IWakeUpFunction>(_ => wakeUpFunctionMock.Object);
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

        return (host, scheduleSvcMock, jobServiceMock);
    }

    #endregion

    #region POST /job-schedules/{name} -----------------------------------------------------------

    [Fact(DisplayName = "POST /job-schedules/{name} – succès ⇒ 202 + corps JSON")]
    public async Task CreateSchedule_Returns_202_When_Success()
    {
        var schedResult = new CreateScheduleJobResult("new-id");

        (IHost host, Mock<IScheduleJobService> schedSvc, _) =
            await BuildHostAsync(svc =>
            {
                svc.Setup(s => s.CreateScheduleJobAsync(
                        "daisy",
                        It.IsAny<ScheduleCreateJob>(),
                        It.IsAny<bool>()))
                   .ReturnsAsync(new ResultWithError<CreateScheduleJobResult>(schedResult));
            });

        var body = new ScheduleCreateJob("* * * * *", new() { "arg1" });
        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync("http://localhost:5000/job-schedules/daisy",
                       JsonContent.Create(body));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("{\"Id\":\"new-id\"}", await resp.Content.ReadAsStringAsync());

        schedSvc.Verify(s => s.CreateScheduleJobAsync("daisy",
                It.IsAny<ScheduleCreateJob>(),
                It.IsAny<bool>()),
            Times.Once);
    }

    [Fact(DisplayName = "POST /job-schedules/{name} – erreur service ⇒ 400")]
    public async Task CreateSchedule_Returns_400_On_Service_Error()
    {
        (IHost host, Mock<IScheduleJobService> schedSvc, _) =
            await BuildHostAsync(svc =>
            {
                svc.Setup(s => s.CreateScheduleJobAsync(
                        "daisy",
                        It.IsAny<ScheduleCreateJob>(),
                        It.IsAny<bool>()))
                   .ReturnsAsync(new ResultWithError<CreateScheduleJobResult>(null,
                       new ErrorResult("image_not_allowed")));
            });

        var body = new ScheduleCreateJob("* * * * *", new() { "arg1" });
        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync("http://localhost:5000/job-schedules/daisy",
                       JsonContent.Create(body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("", await resp.Content.ReadAsStringAsync());

        schedSvc.Verify(s => s.CreateScheduleJobAsync("daisy",
                It.IsAny<ScheduleCreateJob>(),
                It.IsAny<bool>()),
            Times.Once);
    }

    #endregion

    #region POST /job-schedules/{name} - Validation du nom de fonction

    [Theory(DisplayName = "POST /job-schedules/{name} – retourne 400 si le nom de fonction est invalide")]
    [InlineData("/job-schedules/ab")]
    [InlineData("/job-schedules/abcdefghijklmnopqrstuvwxyz12345")]
    [InlineData("/job-schedules/test.func")]
    [InlineData("/job-schedules/test func")]
    [InlineData("/job-schedules/test@func")]
    public async Task CreateSchedule_Returns_400_When_FunctionName_Invalid(string path)
    {
        (IHost host, Mock<IScheduleJobService> schedSvc, _) =
            await BuildHostAsync(svc =>
            {
                // Le service ne devrait jamais être appelé
            });

        var body = new ScheduleCreateJob("* * * * *", new() { "arg1" });
        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync($"http://localhost:5000{path}",
                       JsonContent.Create(body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        schedSvc.Verify(s => s.CreateScheduleJobAsync(
                It.IsAny<string>(),
                It.IsAny<ScheduleCreateJob>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Theory(DisplayName = "POST /job-schedules/{name} – accepte les noms valides")]
    [InlineData("/job-schedules/abc")]
    [InlineData("/job-schedules/abcdefghijkl")]
    [InlineData("/job-schedules/abcdefghijklmnopqrstuvwxyz1234")]
    [InlineData("/job-schedules/test-func")]
    [InlineData("/job-schedules/my-app")]
    [InlineData("/job-schedules/daisy")]
    [InlineData("/job-schedules/ab1")]
    [InlineData("/job-schedules/test_func")]
    [InlineData("/job-schedules/abc_123")]
    public async Task CreateSchedule_Accepts_Valid_FunctionNames(string path)
    {
        var schedResult = new CreateScheduleJobResult("new-id");

        (IHost host, Mock<IScheduleJobService> schedSvc, _) =
            await BuildHostAsync(svc =>
            {
                svc.Setup(s => s.CreateScheduleJobAsync(
                        It.IsAny<string>(),
                        It.IsAny<ScheduleCreateJob>(),
                        It.IsAny<bool>()))
                   .ReturnsAsync(new ResultWithError<CreateScheduleJobResult>(schedResult));
            });

        var body = new ScheduleCreateJob("* * * * *", new() { "arg1" });
        HttpResponseMessage resp = await host.GetTestClient()
            .PostAsync($"http://localhost:5000{path}",
                       JsonContent.Create(body));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        schedSvc.Verify(s => s.CreateScheduleJobAsync(
                It.IsAny<string>(),
                It.IsAny<ScheduleCreateJob>(),
                It.IsAny<bool>()),
            Times.Once);
    }

    #endregion

    #region GET /job-schedules/{name} ------------------------------------------------------------

    [Fact(DisplayName = "GET /job-schedules/{name} – retourne la liste")]
    public async Task ListSchedules_Returns_Snapshot()
    {
        List<ListScheduleJob> expected = new()
        {
            new ListScheduleJob("sid",
                "0 0 * * *",
                ["a"])
        };

        string expectedJson =
            "[{\"Id\":\"sid\",\"Schedule\":\"0 0 * * *\",\"Args\":[\"a\"],\"Image\":\"\",\"BackoffLimit\":1,\"TtlSecondsAfterFinished\":60,\"RestartPolicy\":\"Never\"}]";

        (IHost host, Mock<IScheduleJobService> schedSvc, _) =
            await BuildHostAsync(svc =>
            {
                svc.Setup(s => s.ListScheduleJobAsync("daisy"))
                   .ReturnsAsync(expected);
            });

        HttpResponseMessage resp = await host.GetTestClient()
            .GetAsync("http://localhost:5000/job-schedules/daisy");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string json = await resp.Content.ReadAsStringAsync();
        Assert.Equal(expectedJson, json);

        schedSvc.Verify(s => s.ListScheduleJobAsync("daisy"), Times.Once);
    }

    #endregion

    #region DELETE /job-schedules/{name}/{id} ----------------------------------------------------

    [Theory(DisplayName = "DELETE /job-schedules/{name}/{id} – succès 204 / not-found 404")]
    [InlineData("/job-schedules/daisy/sid", true,  HttpStatusCode.NoContent )]
    [InlineData("/job-schedules/daisy/missing", false, HttpStatusCode.NotFound )]
    public async Task DeleteSchedule_Returns_Expected_Status(string path,
        bool deleteSucceeded,
        HttpStatusCode expectedStatus)
    {
        string scheduleId = path.Split('/').Last();

        (IHost host, Mock<IScheduleJobService> schedSvc, _) =
            await BuildHostAsync(svc =>
            {
                if (deleteSucceeded)
                {
                    svc.Setup(s => s.DeleteScheduleJobAsync("daisy", scheduleId, false))
                       .ReturnsAsync(new ResultWithError<string>(scheduleId));
                }
                else
                {
                    svc.Setup(s => s.DeleteScheduleJobAsync("daisy", scheduleId, false))
                       .ReturnsAsync(new ResultWithError<string>(null,
                           new ErrorResult(ScheduleJobService.NotFound)));
                }
            });

        HttpResponseMessage resp = await host.GetTestClient()
            .DeleteAsync($"http://localhost:5000{path}");

        Assert.Equal(expectedStatus, resp.StatusCode);
        schedSvc.Verify(s => s.DeleteScheduleJobAsync("daisy", scheduleId, false), Times.Once);
    }

    #endregion

    #region DELETE /job-schedules/{name}/{id} - Validation du nom de fonction

    [Theory(DisplayName = "DELETE /job-schedules/{name}/{id} – retourne 400 si le nom de fonction est invalide")]
    [InlineData("/job-schedules/ab/sid")]
    [InlineData("/job-schedules/abcdefghijklmnopqrstuvwxyz12345/sid")]
    public async Task DeleteSchedule_Returns_400_When_FunctionName_Invalid(string path)
    {
        (IHost host, Mock<IScheduleJobService> schedSvc, _) =
            await BuildHostAsync(svc =>
            {
                // Le service ne devrait jamais être appelé
            });

        HttpResponseMessage resp = await host.GetTestClient()
            .DeleteAsync($"http://localhost:5000{path}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        schedSvc.Verify(s => s.DeleteScheduleJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Theory(DisplayName = "DELETE /job-schedules/{name}/{id} – accepte les noms valides")]
    [InlineData("/job-schedules/abc/sid")]
    [InlineData("/job-schedules/test-func/sid")]
    [InlineData("/job-schedules/ab1/sid")]
    [InlineData("/job-schedules/test_func/sid")]
    public async Task DeleteSchedule_Accepts_Valid_FunctionNames(string path)
    {
        string scheduleId = path.Split('/').Last();

        (IHost host, Mock<IScheduleJobService> schedSvc, _) =
            await BuildHostAsync(svc =>
            {
                svc.Setup(s => s.DeleteScheduleJobAsync(It.IsAny<string>(), scheduleId, false))
                   .ReturnsAsync(new ResultWithError<string>(null,
                       new ErrorResult(ScheduleJobService.NotFound)));
            });

        HttpResponseMessage resp = await host.GetTestClient()
            .DeleteAsync($"http://localhost:5000{path}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        schedSvc.Verify(s => s.DeleteScheduleJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()),
            Times.Once);
    }

    #endregion

    #region PUT/PATCH non autorisés --------------------------------------------------------------

    [Theory(DisplayName = "PUT/PATCH /job-schedules/{name} ⇒ 405")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Unsupported_Methods_Return_405(string method)
    {
        (IHost host, _, _) = await BuildHostAsync(_ => { /* aucune config */ });

        HttpRequestMessage req = new(new HttpMethod(method),
            "http://localhost:5000/job-schedules/daisy");

        HttpResponseMessage resp = await host.GetTestClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    #endregion
}
