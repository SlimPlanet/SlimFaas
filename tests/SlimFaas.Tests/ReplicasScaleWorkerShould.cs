using System.Collections;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests;

[CollectionDefinition("ScaleWorker", DisableParallelization = true)]
public class ScaleWorkerCollection { }

[Collection("ScaleWorker")]
public class  ReplicasScaleDeploymentsTestData : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return new object[]
        {
            new DeploymentsInformations(new List<DeploymentInformation>(),
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()), new List<PodInformation>()),
            Times.Never(),
            Times.Never()
        };
        yield return new object[]
        {
            new DeploymentsInformations(
                new List<DeploymentInformation>
                {
                    new("fibonacci1", "default", Replicas: 1, Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration()),
                    new("fibonacci2", "default", Replicas: 0, Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration())
                },
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
                new List<PodInformation>()
            ),
            Times.AtLeastOnce(),
            Times.AtLeastOnce()
        };
        yield return new object[]
        {
            new DeploymentsInformations(
                new List<DeploymentInformation>
                {
                    new("fibonacci1", "default", Replicas: 1, Pods: new List<PodInformation>() { new PodInformation("fibonacci1", true, true, "localhost", "fibonacci1")}, Configuration: new SlimFaasConfiguration()),
                    new("fibonacci2", "default", Replicas: 0, Pods: new List<PodInformation>(), DependsOn: new List<string> { "fibonacci1" }, Configuration: new SlimFaasConfiguration())
                },
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
                new List<PodInformation>()
            ),
            Times.AtLeastOnce(),
            Times.Never()
        };
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class ReplicasScaleWorkerShould
{
    [Theory]
    [ClassData(typeof(ReplicasScaleDeploymentsTestData))]
    public async Task ScaleFunctionUpAndDown(DeploymentsInformations deploymentsInformations, Times scaleUpTimes,
        Times scaleDownTimes)
    {
        Mock<ILogger<ScaleReplicasWorker>> logger = new();
        Mock<IKubernetesService> kubernetesService = new();
        Mock<IMasterService> masterService = new();
        HistoryHttpMemoryService historyHttpService = new();
        historyHttpService.SetTickLastCall("fibonacci2", DateTime.UtcNow.Ticks);
        Mock<ILogger<ReplicasService>> loggerReplicasService = new();
        ReplicasService replicasService =
            new(kubernetesService.Object,
                historyHttpService,
                loggerReplicasService.Object);
        masterService.Setup(ms => ms.IsMaster).Returns(true);
        kubernetesService.Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>())).ReturnsAsync(deploymentsInformations);

        ReplicaRequest scaleRequestFibonacci1 = new("fibonacci1", "default", 0, PodType.Deployment);
        kubernetesService.Setup(k => k.ScaleAsync(scaleRequestFibonacci1)).ReturnsAsync(scaleRequestFibonacci1);
        ReplicaRequest scaleRequestFibonacci2 = new("fibonacci2", "default", 1, PodType.Deployment);
        kubernetesService.Setup(k => k.ScaleAsync(scaleRequestFibonacci2)).ReturnsAsync(scaleRequestFibonacci2);
        await replicasService.SyncDeploymentsAsync("default");

        ScaleReplicasWorker service = new(replicasService, masterService.Object, logger.Object, 100);
        using var cts = new CancellationTokenSource();
        Task task = service.StartAsync(cts.Token);
        await Task.Delay(3000);

        kubernetesService.Verify(v => v.ScaleAsync(scaleRequestFibonacci2), scaleUpTimes);
        kubernetesService.Verify(v => v.ScaleAsync(scaleRequestFibonacci1), scaleDownTimes);

        await cts.CancelAsync();
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task LogErrorWhenExceptionIsThrown()
    {
        Mock<ILogger<ScaleReplicasWorker>> logger = new();
        Mock<IMasterService> masterService = new();
        masterService.Setup(ms => ms.IsMaster).Returns(true);
        Mock<IReplicasService> replicaService = new();
        replicaService.Setup(r => r.CheckScaleAsync(It.IsAny<string>())).Throws(new Exception());

        HistoryHttpMemoryService historyHttpService = new();
        historyHttpService.SetTickLastCall("fibonacci2", DateTime.UtcNow.Ticks);

        ScaleReplicasWorker service = new(replicaService.Object, masterService.Object, logger.Object, 10);
        using var cts = new CancellationTokenSource();
        Task task = service.StartAsync(cts.Token);
        await Task.Delay(100);

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.AtLeastOnce);

        await cts.CancelAsync();
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }


    [Fact]
    public void GetTimeoutSecondBeforeSetReplicasMin()
    {
        var deplymentInformation = new DeploymentInformation("fibonacci1",
            "default",
            Replicas: 1,
            Configuration: new SlimFaasConfiguration(),
            Pods: new List<PodInformation>()
            {
                new PodInformation("fibonacci1", true, true, "localhost", "fibonacci1")
            },
            Schedule: new ScheduleConfig()
            {
                TimeZoneID = "Europe/Paris",
                Default = new DefaultSchedule()
                {
                    ScaleDownTimeout = new List<ScaleDownTimeout>()
                    {
                        new() { Time = "8:00", Value = 60 }, new() { Time = "21:00", Value = 10 },
                    }
                }
            }
        );

        var now = DateTime.UtcNow;
        now = now.AddHours(- (now.Hour - 9));
        var timeout = ReplicasService.GetTimeoutSecondBeforeSetReplicasMin(deplymentInformation, now);
        Assert.Equal(60, timeout);

        now = now.AddHours(- (now.Hour - 22));
        timeout = ReplicasService.GetTimeoutSecondBeforeSetReplicasMin(deplymentInformation, now);
        Assert.Equal(10, timeout);
    }

    [Fact]
    public void GetLastTicksFromSchedule()
    {
        var deploymentInformation = new DeploymentInformation("fibonacci1",
            "default",
            Replicas: 1,
            Configuration: new SlimFaasConfiguration(),
            Pods: new List<PodInformation>()
            {
                new PodInformation("fibonacci1", true, true, "localhost", "fibonacci1")
            },
            Schedule: new ScheduleConfig()
            {
                TimeZoneID = "Europe/Paris",
                Default = new DefaultSchedule()
                {
                    WakeUp = new List<string>()
                    {
                        "8:00",
                        "21:00"
                    }
                }
            }
        );

        var now = DateTime.UtcNow;
        now = now.AddHours(- (now.Hour - 9));
        var ticks = ReplicasService.GetLastTicksFromSchedule(deploymentInformation, now);
        var dateTimeFromTicks = new DateTime(ticks ?? 0, DateTimeKind.Utc);
        Assert.True(dateTimeFromTicks.Hour < 12);

        now = now.AddHours(- (now.Hour - 22));
        ticks = ReplicasService.GetLastTicksFromSchedule(deploymentInformation, now);
        var dateTimeFromTicks22 = new DateTime(ticks ?? 0, DateTimeKind.Utc);
        Assert.True(dateTimeFromTicks22.Hour > 16);

        now = now.AddHours(- (now.Hour - 1));
        ticks = ReplicasService.GetLastTicksFromSchedule(deploymentInformation, now);
        var dateTimeFromTicks1 = new DateTime(ticks ?? 0, DateTimeKind.Utc);
        Assert.True(dateTimeFromTicks1.Hour > 16);
        Console.WriteLine(dateTimeFromTicks1 - dateTimeFromTicks22);
        Assert.True(dateTimeFromTicks1 - dateTimeFromTicks22 < TimeSpan.FromHours(23));
    }
}
