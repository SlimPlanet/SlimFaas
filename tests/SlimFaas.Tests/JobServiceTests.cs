using Xunit;
using Moq;
using System.Collections.Generic;
using System.Threading.Tasks;
using SlimFaas;
using SlimFaas.Kubernetes;
using System;
using System.Linq;
using MemoryPack; // pour vérifier éventuellement la sérialisation si besoin

namespace SlimFaas.Tests
{
    public class JobServiceTests
    {
        private readonly Mock<IKubernetesService> _kubernetesServiceMock;
        private readonly Mock<IJobConfiguration> _jobConfigurationMock;
        private readonly Mock<IJobQueue> _jobQueueMock;
        private readonly JobService _jobService;

        public JobServiceTests()
        {
            _kubernetesServiceMock = new Mock<IKubernetesService>();
            _jobConfigurationMock = new Mock<IJobConfiguration>();
            _jobQueueMock = new Mock<IJobQueue>();

            // Configuration par défaut pour le mock du jobConfiguration
            _jobConfigurationMock.Setup(x => x.Configuration)
                .Returns(new SlimfaasJobConfiguration(new Dictionary<string, SlimfaasJob>
                {
                    {
                        "Default",
                        new SlimfaasJob(
                            Image: "default-image",
                            ImagesWhitelist: new List<string>{ "default-image" },
                            Resources: new CreateJobResources(
                                new Dictionary<string,string>{{ "cpu", "100m" }},
                                new Dictionary<string,string>{{ "cpu", "200m" }}
                            ),
                            Visibility: nameof(FunctionVisibility.Private)
                        )
                    },
                    {
                        "MyPublicJob",
                        new SlimfaasJob(
                            Image: "public-image",
                            ImagesWhitelist: new List<string>{ "public-image", "extra-image" },
                            Resources: new CreateJobResources(
                                new Dictionary<string,string>{{ "cpu", "250m" }},
                                new Dictionary<string,string>{{ "cpu", "500m" }}
                            ),
                            Visibility: nameof(FunctionVisibility.Public)
                        )
                    }
                }));

            // Instanciation de la classe à tester
            _jobService = new JobService(
                _kubernetesServiceMock.Object,
                _jobConfigurationMock.Object,
                _jobQueueMock.Object
            );
        }

        #region CreateJobAsync

        [Fact]
        public async Task CreateJobAsync_ShouldCallKubernetesService_WithCorrectParameters()
        {
            // Arrange
            var jobName = "TestJob";
            var createJob = new CreateJob(new List<string> { "arg1", "arg2" },"some-image");

            // Act
            await _jobService.CreateJobAsync(jobName, createJob);

            // Assert
            _kubernetesServiceMock
                .Verify(x => x.CreateJobAsync(
                    It.IsAny<string>(), // le namespace
                    jobName,
                    createJob),
                Times.Once);
        }

        #endregion

        #region EnqueueJobAsync

        [Fact]
        public async Task EnqueueJobAsync_ShouldReturnError_IfVisibilityIsPrivateAndMessageNotFromNamespaceInternal()
        {
            // Arrange
            // On utilise la config par défaut, où "Default" a Visibility = Private
            var jobName = "Default";
            var createJob = new CreateJob(new List<string>(), "default-image");
            bool isMessageComeFromNamespaceInternal = false;

            // Act
            var result = await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

            // Assert
            Assert.Equal("Visibility private", result.ErrorKey);
            Assert.Equal(400, result.Code);

            // EnqueueAsync ne doit pas être appelé dans ce scénario
            _jobQueueMock.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
        }

        [Fact]
        public async Task EnqueueJobAsync_ShouldReturnError_IfImageIsNotInWhitelist()
        {
            // Arrange
            // Test sur un job "MyPublicJob" qui autorise "public-image" et "extra-image".
            var jobName = "MyPublicJob";
            var createJob = new CreateJob( new List<string>(), "not-allowed-image");
            bool isMessageComeFromNamespaceInternal = true; // même si c'est private, on s'en fiche, c'est un job public

            // Act
            var result = await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

            // Assert
            Assert.Equal("Image_not_allowed", result.ErrorKey);
            Assert.Equal(400, result.Code);

            // EnqueueAsync ne doit pas être appelé
            _jobQueueMock.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
        }

        [Fact]
        public async Task EnqueueJobAsync_ShouldUseDefault_WhenJobNameNotInConfiguration()
        {
            // Arrange
            // "UnknownJob" ne fait pas partie de la config, il doit donc basculer sur "Default"
            var jobName = "UnknownJob";
            var createJob = new CreateJob(new List<string> { "arg1" }, "default-image");
            bool isMessageComeFromNamespaceInternal = true;

            // Act
            var result = await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

            // Assert
            Assert.True(string.IsNullOrEmpty(result.ErrorKey), "Aucune erreur ne doit remonter.");
            Assert.Equal(204, result.Code);

            // Vérifie que EnqueueAsync est bien appelé
            _jobQueueMock.Verify(x => x.EnqueueAsync(
                "Default",
                It.IsAny<byte[]>()),
                Times.Once
            );
        }

        private bool ValidateSerializedJob(byte[] bytes, string expectedImage)
        {
            var jobDeserialized = MemoryPackSerializer.Deserialize<CreateJob>(bytes);
            return jobDeserialized != null && jobDeserialized.Image == expectedImage;
        }


        [Fact]
        public async Task EnqueueJobAsync_ShouldFallbackToConfiguredImage_IfCreateJobImageIsEmpty()
        {
            // Arrange
            // On utilise "MyPublicJob" dont l'image par défaut est "public-image".
            var jobName = "MyPublicJob";
            var createJob = new CreateJob(new List<string>(), "");
            bool isMessageComeFromNamespaceInternal = true;

            // Act
            var result = await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

            // Assert
            Assert.True(string.IsNullOrEmpty(result.ErrorKey));
            Assert.Equal(204, result.Code);

            // On peut vérifier le contenu de l’enqueue pour voir si la sérialisation a bien l'image fallback "public-image"
            _jobQueueMock.Verify(x => x.EnqueueAsync(
                jobName,
                It.Is<byte[]>(bytes => ValidateSerializedJob(bytes, "public-image"))
            ), Times.Once);
        }

        private bool ValidateSerializedEnvironments(byte[] bytes)
        {
            var jobDeserialized = MemoryPackSerializer.Deserialize<CreateJob>(bytes);
            if (jobDeserialized?.Environments == null) return false;

            var envDict = jobDeserialized.Environments.ToDictionary(e => e.Name, e => e.Value);

            return envDict.TryGetValue("ENV_EXISTING", out var existingValue) && existingValue == "ExistingValue"
                                                                              && envDict.TryGetValue("ENV_NEW", out var newValue) && newValue == "NewValue"
                                                                              && envDict.TryGetValue("ENV_COMMON", out var commonValue) && commonValue == "OverriddenValue";
        }


        [Fact]
        public async Task EnqueueJobAsync_ShouldMergeEnvironmentsCorrectly()
        {
            // Arrange
            // On configure "MyPublicJob" pour qu'il ait déjà un environment d'exemple.
            var currentConfig = _jobConfigurationMock.Object.Configuration.Configurations["MyPublicJob"];
            var newConfig = currentConfig with
            {
                Environments = new List<EnvVarInput>
                {
                    new("ENV_EXISTING", "ExistingValue"),
                    new("ENV_COMMON", "OldValue")
                }
            };

            // On met à jour la configuration en dur
            _jobConfigurationMock.Setup(x => x.Configuration)
                .Returns(new SlimfaasJobConfiguration(new Dictionary<string, SlimfaasJob>
                {
                    { "MyPublicJob", newConfig }
                }));

            var jobName = "MyPublicJob";
            var createJob = new CreateJob(new List<string>(), "public-image", Environments:  new List<EnvVarInput>
                {
                    new("ENV_NEW", "NewValue"),
                    new("ENV_COMMON", "OverriddenValue") // remplace l'ancienne
                }
            );
            bool isMessageComeFromNamespaceInternal = true;

            // Act
            var result = await _jobService.EnqueueJobAsync(jobName, createJob, isMessageComeFromNamespaceInternal);

            // Assert
            Assert.True(string.IsNullOrEmpty(result.ErrorKey));
            Assert.Equal(204, result.Code);

            // Vérifie qu'on a ENV_EXISTING, ENV_NEW et ENV_COMMON (avec la nouvelle valeur)
            _jobQueueMock.Verify(x => x.EnqueueAsync(
                jobName,
                It.Is<byte[]>(bytes => ValidateSerializedEnvironments(bytes))
            ), Times.Once);
        }

        #endregion

    }
}
