﻿﻿using System.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests;

[CollectionDefinition("ScaleWorker", DisableParallelization = true)]
public class ScaleWorkerCollection { }

[Collection("ScaleWorker")]
public class ReplicasScaleDeploymentsTestData : IEnumerable<object[]>
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
                    new("fibonacci1", "default", Replicas: 1,
                        Pods: new List<PodInformation>
                        {
                            new("fibonacci1", true, true, "localhost", "fibonacci1")
                        },
                        Configuration: new SlimFaasConfiguration()),
                    new("fibonacci2", "default", Replicas: 0,
                        Pods: new List<PodInformation>(),
                        DependsOn: new List<string> { "fibonacci1" },
                        Configuration: new SlimFaasConfiguration())
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
    private static AutoScaler CreateAutoScalerForTests()
    {
        PromQlMiniEvaluator.SnapshotProvider snapshotProvider = () =>
        {
            var metrics = new Dictionary<string, double> { { "dummy_metric", 1.0 } };
            var pod = new Dictionary<string, IReadOnlyDictionary<string, double>>
            {
                { "pod-0", metrics }
            };
            var deployment = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
            {
                { "dummy-deploy", pod }
            };
            var root = new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>
            {
                { 1L, deployment }
            };
            return root;
        };

        var evaluator = new PromQlMiniEvaluator(snapshotProvider);
        var store = new InMemoryAutoScalerStore();
        return new AutoScaler(evaluator, store, logger: null);
    }

    [Theory]
    [ClassData(typeof(ReplicasScaleDeploymentsTestData))]
    public async Task ScaleFunctionUpAndDown(
        DeploymentsInformations deploymentsInformations,
        Times scaleUpTimes,
        Times scaleDownTimes)
    {
        var logger = new Mock<ILogger<ScaleReplicasWorker>>();
        var kubernetesService = new Mock<IKubernetesService>();
        var masterService = new Mock<IMasterService>();
        var historyHttpService = new HistoryHttpMemoryService();

        // On force "fibonacci2" comme récemment appelé pour déclencher le scale-up 0 -> 1
        historyHttpService.SetTickLastCall("fibonacci2", DateTime.UtcNow.Ticks);

        var loggerReplicasService = new Mock<ILogger<ReplicasService>>();
        var autoScaler = CreateAutoScalerForTests();

        // Nouveau : registry dummy pour coller à la signature de ReplicasService
        var metricsRegistry = new Mock<IRequestedMetricsRegistry>().Object;

        var slimFaasOptionsForReplicas = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            PodScaledUpByDefaultWhenInfrastructureHasNeverCalled = false
        });

        var replicasService = new ReplicasService(
            kubernetesService.Object,
            historyHttpService,
            autoScaler,
            loggerReplicasService.Object,
            metricsRegistry,
            slimFaasOptionsForReplicas);

        masterService.Setup(ms => ms.IsMaster).Returns(true);

        kubernetesService
            .Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(deploymentsInformations);

        // Scale down fibonacci1 -> 0
        var scaleRequestFibonacci1 = new ReplicaRequest("fibonacci1", "default", 0, PodType.Deployment);
        kubernetesService
            .Setup(k => k.ScaleAsync(scaleRequestFibonacci1))
            .ReturnsAsync(scaleRequestFibonacci1);

        // Scale up fibonacci2 -> 1
        var scaleRequestFibonacci2 = new ReplicaRequest("fibonacci2", "default", 1, PodType.Deployment);
        kubernetesService
            .Setup(k => k.ScaleAsync(scaleRequestFibonacci2))
            .ReturnsAsync(scaleRequestFibonacci2);

        await replicasService.SyncDeploymentsAsync("default");

        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            ScaleReplicasDelayMilliseconds = 100
        });
        var slimFaasOptions = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            Namespace = "default"
        });

        var namespaceProviderMock = new Mock<INamespaceProvider>();
        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");

        var service = new ScaleReplicasWorker(
            replicasService,
            masterService.Object,
            logger.Object,
            slimFaasOptions,
            workersOptions,
            namespaceProviderMock.Object);

        using var cts = new CancellationTokenSource();
        var task = service.StartAsync(cts.Token);

        await Task.Delay(3000);

        // Vérification du scale up/down selon les cas de test
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

        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            ScaleReplicasDelayMilliseconds = 10
        });
        var slimFaasOptions = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            Namespace = "default"
        });

        var namespaceProviderMock = new Mock<INamespaceProvider>();
        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");

        ScaleReplicasWorker service = new(replicaService.Object, masterService.Object, logger.Object, slimFaasOptions, workersOptions, namespaceProviderMock.Object);
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
            Pods: new List<PodInformation>
            {
                new("fibonacci1", true, true, "localhost", "fibonacci1")
            },
            Schedule: new ScheduleConfig
            {
                TimeZoneID = "Europe/Paris",
                Default = new DefaultSchedule
                {
                    ScaleDownTimeout = new List<ScaleDownTimeout>
                    {
                        new() { Time = "8:00", Value = 60 },
                        new() { Time = "21:00", Value = 10 },
                    }
                }
            }
        );

        var now = DateTime.UtcNow;
        now = now.AddHours(-(now.Hour - 9));
        var timeout = ReplicasService.GetTimeoutSecondBeforeSetReplicasMin(deplymentInformation, now);
        Assert.Equal(60, timeout);

        now = now.AddHours(-(now.Hour - 22));
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
            Pods: new List<PodInformation>
            {
                new("fibonacci1", true, true, "localhost", "fibonacci1")
            },
            Schedule: new ScheduleConfig
            {
                TimeZoneID = "Europe/Paris",
                Default = new DefaultSchedule
                {
                    WakeUp = new List<string>
                    {
                        "8:00",
                        "21:00"
                    }
                }
            }
        );

        var now = DateTime.UtcNow;
        now = now.AddHours(-(now.Hour - 9));
        var ticks = ReplicasService.GetLastTicksFromSchedule(deploymentInformation, now);
        var dateTimeFromTicks = new DateTime(ticks ?? 0, DateTimeKind.Utc);
        Assert.True(dateTimeFromTicks.Hour < 12);

        now = now.AddHours(-(now.Hour - 22));
        ticks = ReplicasService.GetLastTicksFromSchedule(deploymentInformation, now);
        var dateTimeFromTicks22 = new DateTime(ticks ?? 0, DateTimeKind.Utc);
        Assert.True(dateTimeFromTicks22.Hour > 16);

        now = now.AddHours(-(now.Hour - 1));
        ticks = ReplicasService.GetLastTicksFromSchedule(deploymentInformation, now);
        var dateTimeFromTicks1 = new DateTime(ticks ?? 0, DateTimeKind.Utc);
        Assert.True(dateTimeFromTicks1.Hour > 16);
        Console.WriteLine(dateTimeFromTicks1 - dateTimeFromTicks22);
        Assert.True(dateTimeFromTicks1 - dateTimeFromTicks22 < TimeSpan.FromHours(23));
    }

        [Fact]
    public async Task NotScaleUpWhenPodIsBlockedByQuota()
    {
        var loggerReplicasService = new Mock<ILogger<ReplicasService>>();
        var kubernetesService = new Mock<IKubernetesService>();
        var historyHttpService = new HistoryHttpMemoryService();
        var autoScaler = CreateAutoScalerForTests();
        var metricsRegistry = new Mock<IRequestedMetricsRegistry>().Object;

        // Deployment avec 0 replicas, mais un pod bloqué par "exceeded quota"
        var deployment = new DeploymentInformation(
            Deployment: "fibonacci-quota",
            Namespace: "default",
            Pods: new List<PodInformation>
            {
                new("fibonacci-quota-0", Started: false, Ready: false, Ip: "10.0.0.1", DeploymentName: "fibonacci-quota")
                {
                    StartFailureMessage = "0/3 nodes are available: 3 exceeded quota: pods.",
                    StartFailureReason = "exceeded quota"
                }
            },
            Configuration: new SlimFaasConfiguration(),
            Replicas: 0 // => tentative 0 -> 1
        );

        var deploymentsInformations = new DeploymentsInformations(
            new List<DeploymentInformation> { deployment },
            new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
            new List<PodInformation>());

        kubernetesService
            .Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(deploymentsInformations);

        // Dernier appel HTTP "maintenant" => pas de scale down, on devrait sortir de 0 -> 1
        historyHttpService.SetTickLastCall("fibonacci-quota", DateTime.UtcNow.Ticks);

        var slimFaasOptionsForReplicas = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            PodScaledUpByDefaultWhenInfrastructureHasNeverCalled = false
        });

        var replicasService = new ReplicasService(
            kubernetesService.Object,
            historyHttpService,
            autoScaler,
            loggerReplicasService.Object,
            metricsRegistry,
            slimFaasOptionsForReplicas);

        await replicasService.SyncDeploymentsAsync("default");
        await replicasService.CheckScaleAsync("default");

        // ✅ On ne doit PAS appeler ScaleAsync car le pod est bloqué par un quota
        kubernetesService.Verify(k => k.ScaleAsync(It.IsAny<ReplicaRequest>()), Times.Never);
    }

        [Fact]
    public async Task ScaleDownEvenWhenPodIsBlockedByQuota()
    {
        var loggerReplicasService = new Mock<ILogger<ReplicasService>>();
        var kubernetesService = new Mock<IKubernetesService>();
        var historyHttpService = new HistoryHttpMemoryService();
        var autoScaler = CreateAutoScalerForTests();
        var metricsRegistry = new Mock<IRequestedMetricsRegistry>().Object;

        // Deployment avec 2 replicas, pod bloqué par quota => on veut quand même autoriser le scale down
        var deployment = new DeploymentInformation(
            Deployment: "fibonacci-quota",
            Namespace: "default",
            Pods: new List<PodInformation>
            {
                new("fibonacci-quota-0", Started: false, Ready: false, Ip: "10.0.0.1", DeploymentName: "fibonacci-quota")
                {
                    StartFailureMessage = "0/3 nodes are available: 3 exceeded quota: pods.",
                    StartFailureReason = "exceeded quota"
                }
            },
            Configuration: new SlimFaasConfiguration(),
            Replicas: 2,
            ReplicasMin: 0
        );

        var deploymentsInformations = new DeploymentsInformations(
            new List<DeploymentInformation> { deployment },
            new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
            new List<PodInformation>());

        kubernetesService
            .Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(deploymentsInformations);

        // Dernier appel HTTP très ancien => on doit déclencher le scale down vers ReplicasMin (0)
        historyHttpService.SetTickLastCall("fibonacci-quota", DateTime.UtcNow.AddMinutes(-10).Ticks);

        var slimFaasOptionsForReplicas = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            PodScaledUpByDefaultWhenInfrastructureHasNeverCalled = false
        });

        var replicasService = new ReplicasService(
            kubernetesService.Object,
            historyHttpService,
            autoScaler,
            loggerReplicasService.Object,
            metricsRegistry,
            slimFaasOptionsForReplicas);

        var expectedRequest = new ReplicaRequest("fibonacci-quota", "default", 0, PodType.Deployment);

        kubernetesService
            .Setup(k => k.ScaleAsync(expectedRequest))
            .ReturnsAsync(expectedRequest);

        await replicasService.SyncDeploymentsAsync("default");
        await replicasService.CheckScaleAsync("default");

        // ✅ Le scale down doit bien être effectué, malgré le quota exceeded
        kubernetesService.Verify(k => k.ScaleAsync(expectedRequest), Times.Once);
    }


}
