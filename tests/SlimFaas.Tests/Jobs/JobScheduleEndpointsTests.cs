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
            .Returns(new List<Job>());

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
                    })
                    .Configure(app => app.UseMiddleware<SlimProxyMiddleware>());
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

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
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

    [Theory(DisplayName = "DELETE /job-schedules/{name}/{id} – succès 200 / not-found 404")]
    [InlineData("/job-schedules/daisy/sid", true,  HttpStatusCode.OK )]
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
