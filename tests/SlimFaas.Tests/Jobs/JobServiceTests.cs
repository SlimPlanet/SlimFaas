﻿using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

// pour vérifier éventuellement la sérialisation si besoin

namespace SlimFaas.Tests;

public class JobServiceTests
{
    private readonly Mock<IJobConfiguration> _jobConfigurationMock;
    private readonly Mock<IJobQueue> _jobQueueMock;
    private readonly JobService _jobService;
    private readonly Mock<IKubernetesService> _kubernetesServiceMock;

    public JobServiceTests()
    {
        _kubernetesServiceMock = new Mock<IKubernetesService>();
        _jobConfigurationMock = new Mock<IJobConfiguration>();
        _jobQueueMock = new Mock<IJobQueue>();

        // Configuration par défaut pour le mock du jobConfiguration
        _jobConfigurationMock.Setup(x => x.Configuration)
            .Returns(new SlimFaasJobConfiguration(new Dictionary<string, SlimfaasJob>
            {
                {
                    "Default", new SlimfaasJob(
                        "default-image",
                        new List<string> { "default-image", "pattern-image:*" },
                        new CreateJobResources(
                            new Dictionary<string, string> { { "cpu", "100m" } },
                            new Dictionary<string, string> { { "cpu", "200m" } }
                        ),
                        Visibility: nameof(FunctionVisibility.Private)
                    )
                },
                {
                    "MyPublicJob", new SlimfaasJob(
                        "public-image",
                        new List<string> { "public-image", "extra-image" },
                        new CreateJobResources(
                            new Dictionary<string, string> { { "cpu", "250m" } },
                            new Dictionary<string, string> { { "cpu", "500m" } }
                        ),
                        Visibility: nameof(FunctionVisibility.Public)
                    )
                }
            }));

        // Instanciation de la classe à tester
        var namespaceProviderMock = new Mock<INamespaceProvider>();
        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");

        _jobService = new JobService(
            _kubernetesServiceMock.Object,
            _jobConfigurationMock.Object,
            _jobQueueMock.Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions { Namespace = "default" }),
            namespaceProviderMock.Object,
            NullLogger<JobService>.Instance
        );
    }

    #region CreateJobAsync

    [Fact]
    public async Task CreateJobAsync_ShouldCallKubernetesService_WithCorrectParameters()
    {
        // Arrange
        string jobName = "TestJob";
        CreateJob createJob = new(["arg1", "arg2"], "some-image");
        CreateJob createJobPattern = new(["arg1", "arg2"], "pattern-image:latest");
        string elementId = "1";

        // Act
        await _jobService.CreateJobAsync(jobName, createJob, elementId, $"{jobName}1", 1);
        await _jobService.CreateJobAsync(jobName, createJobPattern, elementId, $"{jobName}2", 2);

        // Assert
        _kubernetesServiceMock
            .Verify(x => x.CreateJobAsync(
                    It.IsAny<string>(), // le namespace
                    jobName,
                    createJob,
                    elementId, jobName + "1", 1),
                Times.Once);

        _kubernetesServiceMock
            .Verify(x => x.CreateJobAsync(
                    It.IsAny<string>(), // le namespace
                    jobName,
                    createJobPattern,
                    elementId, jobName + "2", 2),
                Times.Once);
    }

    #endregion

    #region EnqueueJobAsync

    [Fact]
    public async Task EnqueueJobAsync_ShouldReturnError_IfVisibilityIsPrivateAndMessageNotFromNamespaceInternal()
    {
        // Arrange
        // On utilise la config par défaut, où "Default" a Visibility = Private
        string jobName = "Default";
        CreateJob createJob = new(new List<string>(), "default-image");
        bool isMessageComeFromNamespaceInternal = false;

        // Act
        var result =
            await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

        // Assert
        Assert.Equal("visibility_private", result.Error?.Key);

        // EnqueueAsync ne doit pas être appelé dans ce scénario
        _jobQueueMock.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueJobAsync_ShouldReturnError_IfImageIsNotInWhitelist()
    {
        // Arrange
        // Test sur un job "MyPublicJob" qui autorise "public-image" et "extra-image".
        string jobName = "MyPublicJob";
        CreateJob createJob = new(new List<string>(), "not-allowed-image");
        bool isMessageComeFromNamespaceInternal = true; // même si c'est private, on s'en fiche, c'est un job public

        // Act
        var result =
            await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

        // Assert
        Assert.Equal("image_not_allowed", result.Error?.Key);

        // EnqueueAsync ne doit pas être appelé
        _jobQueueMock.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueJobAsync_ShouldUseDefault_WhenJobNameNotInConfiguration()
    {
        // Arrange
        // "UnknownJob" ne fait pas partie de la config, il doit donc basculer sur "Default"
        string jobName = "UnknownJob";
        CreateJob createJob = new(new List<string> { "arg1" }, "default-image");
        bool isMessageComeFromNamespaceInternal = true;

        // Act
        var result =
            await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

        // Assert
        Assert.True(result.IsSuccess, "Aucune erreur ne doit remonter.");

        // Vérifie que EnqueueAsync est bien appelé
        _jobQueueMock.Verify(x => x.EnqueueAsync(
                "Default",
                It.IsAny<byte[]>()),
            Times.Once
        );
    }

    private bool ValidateSerializedJob(byte[] bytes, string expectedImage)
    {
        JobInQueue? jobDeserialized = MemoryPackSerializer.Deserialize<JobInQueue>(bytes);
        return jobDeserialized != null && jobDeserialized.CreateJob.Image == expectedImage;
    }


    [Fact]
    public async Task EnqueueJobAsync_ShouldFallbackToConfiguredImage_IfCreateJobImageIsEmpty()
    {
        // Arrange
        // On utilise "MyPublicJob" dont l'image par défaut est "public-image".
        string jobName = "MyPublicJob";
        CreateJob createJob = new(new List<string>());
        bool isMessageComeFromNamespaceInternal = true;

        // Act
        var result =
            await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

        // Assert
        Assert.True(result.IsSuccess);

        // On peut vérifier le contenu de l’enqueue pour voir si la sérialisation a bien l'image fallback "public-image"
        _jobQueueMock.Verify(x => x.EnqueueAsync(
            jobName,
            It.Is<byte[]>(bytes => ValidateSerializedJob(bytes, "public-image"))
        ), Times.Once);
    }

    private bool ValidateSerializedEnvironments(byte[] bytes)
    {
        JobInQueue? jobDeserialized = MemoryPackSerializer.Deserialize<JobInQueue>(bytes);
        if (jobDeserialized?.CreateJob.Environments == null)
        {
            return false;
        }

        Dictionary<string, string> envDict =
            jobDeserialized.CreateJob.Environments.ToDictionary(e => e.Name, e => e.Value);

        return envDict.TryGetValue("ENV_EXISTING", out string? existingValue) && existingValue == "ExistingValue"
                                                                              && envDict.TryGetValue("ENV_NEW",
                                                                                  out string? newValue) &&
                                                                              newValue == "NewValue"
                                                                              && envDict.TryGetValue("ENV_COMMON",
                                                                                  out string? commonValue) &&
                                                                              commonValue == "OverriddenValue";
    }


    [Fact]
    public async Task EnqueueJobAsync_ShouldMergeEnvironmentsCorrectly()
    {
        // Arrange
        // On configure "MyPublicJob" pour qu'il ait déjà un environment d'exemple.
        SlimfaasJob currentConfig = _jobConfigurationMock.Object.Configuration.Configurations["MyPublicJob"];
        SlimfaasJob newConfig = currentConfig with
        {
            Environments = new List<EnvVarInput>
            {
                new("ENV_EXISTING", "ExistingValue"), new("ENV_COMMON", "OldValue"),
            }
        };

        // On met à jour la configuration en dur
        _jobConfigurationMock.Setup(x => x.Configuration)
            .Returns(new SlimFaasJobConfiguration(new Dictionary<string, SlimfaasJob>
            {
                { "MyPublicJob", newConfig }
            }));

        string jobName = "MyPublicJob";
        CreateJob createJob = new(new List<string>(), "public-image", Environments: new List<EnvVarInput>
            {
                new("ENV_NEW", "NewValue"), new("ENV_COMMON", "OverriddenValue") // remplace l'ancienne
            }
        );
        bool isMessageComeFromNamespaceInternal = true;

        // Act
        var result =
            await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

        // Assert
        Assert.True(result.IsSuccess);

        // Vérifie qu'on a ENV_EXISTING, ENV_NEW et ENV_COMMON (avec la nouvelle valeur)
        _jobQueueMock.Verify(x => x.EnqueueAsync(
            jobName,
            It.Is<byte[]>(bytes => ValidateSerializedEnvironments(bytes))
        ), Times.Once);
    }

    #endregion
}
