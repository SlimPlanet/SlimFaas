using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaasKafka.Config;
using SlimFaasKafka.Kafka;
using SlimFaasKafka.Services;
using SlimFaasKafka.Workers;

namespace SlimFaasKafka.Tests;

public class KafkaMonitoringWorkerTests
{
    private static KafkaOptions CreateKafkaOptions() => new()
    {
        BootstrapServers = "kafka:9092",
        ClientId = "test-client",
        CheckIntervalSeconds = 5,
        KafkaTimeoutSeconds = 5,
        AllowAutoCreateTopics = true
    };

    private static BindingsOptions CreateBindingsOptions(TopicBinding binding)
        => new() { Bindings = new List<TopicBinding> { binding } };

    private static Task InvokeCheckBindingsAsync(
        KafkaMonitoringWorker worker,
        KafkaOptions kafkaOptions,
        BindingsOptions bindingsOptions,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(KafkaMonitoringWorker)
            .GetMethod("CheckBindingsAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        if (method == null)
            throw new InvalidOperationException("CheckBindingsAsync method not found via reflection.");

        var task = (Task)method.Invoke(worker, new object[] { kafkaOptions, bindingsOptions, cancellationToken })!;
        return task;
    }

    [Fact]
    public async Task CheckBindingsAsync_WakesFunction_WhenPendingExceedsThresholdAndNoCooldown()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<KafkaMonitoringWorker>>();

        var binding = new TopicBinding
        {
            Topic = "fibo-public",
            ConsumerGroupId = "group1",
            FunctionName = "func1",
            MinPendingMessages = 10,
            CooldownSeconds = 30,
            ActivityKeepAliveSeconds = 0,
            MinConsumedDeltaForActivity = 1
        };

        var kafkaOptions = CreateKafkaOptions();
        var bindingsOptions = CreateBindingsOptions(binding);

        var kafkaOptionsMonitor = new Mock<IOptionsMonitor<KafkaOptions>>();
        kafkaOptionsMonitor.Setup(x => x.CurrentValue).Returns(kafkaOptions);

        var bindingsOptionsMonitor = new Mock<IOptionsMonitor<BindingsOptions>>();
        bindingsOptionsMonitor.Setup(x => x.CurrentValue).Returns(bindingsOptions);

        var slimFaasClientMock = new Mock<ISlimFaasClient>();

        var lagProviderMock = new Mock<IKafkaLagProvider>();
        lagProviderMock
            .Setup(p => p.GetLagAsync(
                It.Is<TopicBinding>(b => b.FunctionName == "func1"),
                It.IsAny<KafkaOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((pending: 15L, totalCommitted: 0L, usedAdmin: false));

        var worker = new KafkaMonitoringWorker(
            loggerMock.Object,
            kafkaOptionsMonitor.Object,
            bindingsOptionsMonitor.Object,
            slimFaasClientMock.Object,
            lagProviderMock.Object);

        // Act
        await InvokeCheckBindingsAsync(worker, kafkaOptions, bindingsOptions);

        // Assert : pending >= MinPendingMessages => wake une fois
        slimFaasClientMock.Verify(
            c => c.WakeAsync("func1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckBindingsAsync_RespectsCooldown_PerFunction()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<KafkaMonitoringWorker>>();

        var binding = new TopicBinding
        {
            Topic = "fibo-public",
            ConsumerGroupId = "group1",
            FunctionName = "func1",
            MinPendingMessages = 1,
            CooldownSeconds = 60, // long cooldown
            ActivityKeepAliveSeconds = 0,
            MinConsumedDeltaForActivity = 1
        };

        var kafkaOptions = CreateKafkaOptions();
        var bindingsOptions = CreateBindingsOptions(binding);

        var kafkaOptionsMonitor = new Mock<IOptionsMonitor<KafkaOptions>>();
        kafkaOptionsMonitor.Setup(x => x.CurrentValue).Returns(kafkaOptions);

        var bindingsOptionsMonitor = new Mock<IOptionsMonitor<BindingsOptions>>();
        bindingsOptionsMonitor.Setup(x => x.CurrentValue).Returns(bindingsOptions);

        var slimFaasClientMock = new Mock<ISlimFaasClient>();

        var lagProviderMock = new Mock<IKafkaLagProvider>();
        lagProviderMock
            .Setup(p => p.GetLagAsync(
                It.IsAny<TopicBinding>(),
                It.IsAny<KafkaOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((pending: 5L, totalCommitted: 0L, usedAdmin: false));

        var worker = new KafkaMonitoringWorker(
            loggerMock.Object,
            kafkaOptionsMonitor.Object,
            bindingsOptionsMonitor.Object,
            slimFaasClientMock.Object,
            lagProviderMock.Object);

        // Act : premier passage => wake
        await InvokeCheckBindingsAsync(worker, kafkaOptions, bindingsOptions);

        // Act : deuxième passage immédiatement => cooldown actif, pas de nouveau wake
        await InvokeCheckBindingsAsync(worker, kafkaOptions, bindingsOptions);

        // Assert : WakeAsync n'est appelé qu'une seule fois malgré 2 checks
        slimFaasClientMock.Verify(
            c => c.WakeAsync("func1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckBindingsAsync_WakesOnRecentActivity_WhenPendingBelowThreshold()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<KafkaMonitoringWorker>>();

        var binding = new TopicBinding
        {
            Topic = "fibo-public",
            ConsumerGroupId = "group1",
            FunctionName = "func1",
            MinPendingMessages = 100,       // très haut => pending seul ne suffit pas
            CooldownSeconds = 0,
            ActivityKeepAliveSeconds = 60,  // keep-alive basé sur activité
            MinConsumedDeltaForActivity = 1
        };

        var kafkaOptions = CreateKafkaOptions();
        var bindingsOptions = CreateBindingsOptions(binding);

        var kafkaOptionsMonitor = new Mock<IOptionsMonitor<KafkaOptions>>();
        kafkaOptionsMonitor.Setup(x => x.CurrentValue).Returns(kafkaOptions);

        var bindingsOptionsMonitor = new Mock<IOptionsMonitor<BindingsOptions>>();
        bindingsOptionsMonitor.Setup(x => x.CurrentValue).Returns(bindingsOptions);

        var slimFaasClientMock = new Mock<ISlimFaasClient>();

        // usedAdmin = true, committedSum > 0 => activité "réelle"
        var lagProviderMock = new Mock<IKafkaLagProvider>();
        lagProviderMock
            .Setup(p => p.GetLagAsync(
                It.IsAny<TopicBinding>(),
                It.IsAny<KafkaOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((pending: 0L, totalCommitted: 100L, usedAdmin: true));

        var worker = new KafkaMonitoringWorker(
            loggerMock.Object,
            kafkaOptionsMonitor.Object,
            bindingsOptionsMonitor.Object,
            slimFaasClientMock.Object,
            lagProviderMock.Object);

        // Act : même si pending=0 (< MinPendingMessages), l'activité récente
        // + ActivityKeepAliveSeconds > 0 doit déclencher un wake.
        await InvokeCheckBindingsAsync(worker, kafkaOptions, bindingsOptions);

        // Assert
        slimFaasClientMock.Verify(
            c => c.WakeAsync("func1", It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
