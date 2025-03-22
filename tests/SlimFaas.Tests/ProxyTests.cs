using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SlimFaas.Kubernetes;
using Xunit;

namespace SlimFaas.Tests
{

    public class FakeDeploymentCollection
    {
        public List<DeploymentInformation> Functions { get; set; } = new();
    }

    // ===============================
    // 3) Les tests xUnit
    // ===============================
    public class ProxyTests
    {
        /// <summary>
        /// Nettoyage du dictionnaire statique avant chaque test
        /// pour éviter des interférences.
        /// </summary>
        public ProxyTests()
        {
            Proxy.IpAddresses.Clear();
        }

        [Fact]
        public void GetNextIP_WhenFunctionNotFound_ReturnsEmptyString()
        {
            // Arrange
            var replicasService = new FakeReplicasService
            {
                Deployments = new FakeDeploymentCollection { Functions = new List<DeploymentInformation>() }
            };
            var proxy = new Proxy(replicasService, "unknownFunction");

            // Act
            var result = proxy.GetNextIP();

            // Assert
            Assert.Equal("", result);
        }

        [Fact]
        public void GetNextIP_WhenNoPodsReady_ReturnsEmptyString()
        {
            // Arrange
            var replicasService = new FakeReplicasService
            {
                Deployments = new FakeDeploymentCollection
                {
                    Functions = new List<DeploymentInformation>
                    {
                        new(
                            Deployment: "my-deployment",
                            Namespace: "default",
                            Pods: new List<PodInformation>
                            {
                                new("pod1", false, false, "10.0.0.1", "my-deployment"),
                                new("pod2", false, false, "10.0.0.2", "my-deployment")
                            },
                            Configuration: new SlimFaasConfiguration(),
                            Replicas: 1
                        )
                    }
                }
            };
            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            var result = proxy.GetNextIP();

            // Assert
            Assert.Equal("", result);
        }

        [Fact]
        public void GetNextIP_WhenNoLastIpExists_ReturnsRandomPod()
        {
            // Arrange
            var replicasService = new FakeReplicasService
            {
                Deployments = new FakeDeploymentCollection
                {
                    Functions = new List<DeploymentInformation>
                    {
                        new(
                            Deployment: "my-deployment",
                            Namespace: "default",
                            Pods: new List<PodInformation>
                            {
                                new("pod1", true, true, "10.0.0.1", "my-deployment"),
                                new("pod2", true, true, "10.0.0.2", "my-deployment"),
                                new("pod3", true, true, "10.0.0.3", "my-deployment")
                            },
                            Configuration: new SlimFaasConfiguration(),
                            Replicas: 1
                        )
                    }
                }
            };
            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            var result = proxy.GetNextIP();

            // Assert
            Assert.Contains(result, new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" });
            Assert.True(Proxy.IpAddresses.ContainsKey("my-deployment"));
            Assert.Equal(result, Proxy.IpAddresses["my-deployment"]);
        }

        [Fact]
        public void GetNextIP_WhenLastIpExistsInPods_ReturnsNextPodInRoundRobin()
        {
            // Arrange
            var replicasService = new FakeReplicasService
            {
                Deployments = new FakeDeploymentCollection
                {
                    Functions = new List<DeploymentInformation>
                    {
                        new(
                            Deployment: "my-deployment",
                            Namespace: "default",
                            Pods: new List<PodInformation>
                            {
                                new("pod1", true, true, "10.0.0.1", "my-deployment"),
                                new("pod2", true, true, "10.0.0.2", "my-deployment"),
                                new("pod3", true, true, "10.0.0.3", "my-deployment")
                            },
                            Configuration: new SlimFaasConfiguration(),
                            Replicas: 1
                        )
                    }
                }
            };

            // On simule une dernière IP connue
            Proxy.IpAddresses["my-deployment"] = "10.0.0.1";

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            var firstCall = proxy.GetNextIP(); // Devrait être "10.0.0.2"
            var secondCall = proxy.GetNextIP(); // Devrait être "10.0.0.3"

            // Assert
            Assert.Equal("10.0.0.2", firstCall);
            Assert.Equal("10.0.0.3", secondCall);
        }

        [Fact]
        public void GetNextIP_WhenLastIpNotInPods_ReturnsRandomPod()
        {
            // Arrange
            var replicasService = new FakeReplicasService
            {
                Deployments = new FakeDeploymentCollection
                {
                    Functions = new List<DeploymentInformation>
                    {
                        new(
                            Deployment: "my-deployment",
                            Namespace: "default",
                            Pods: new List<PodInformation>
                            {
                                new("pod1", true, true, "10.0.0.1", "my-deployment"),
                                new("pod2", true, true, "10.0.0.2", "my-deployment")
                            },
                            Configuration: new SlimFaasConfiguration(),
                            Replicas: 1
                        )
                    }
                }
            };

            // Dernière IP qui n’existe plus
            Proxy.IpAddresses["my-deployment"] = "99.99.99.99";

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            var result = proxy.GetNextIP();

            // Assert
            Assert.Contains(result, new[] { "10.0.0.1", "10.0.0.2" });
        }

        [Fact]
        public void GetPorts_WhenFunctionNotFound_ReturnsNull()
        {
            // Arrange
            var replicasService = new FakeReplicasService
            {
                Deployments = new FakeDeploymentCollection { Functions = new List<DeploymentInformation>() }
            };
            var proxy = new Proxy(replicasService, "unknownFunction");

            // Act
            var ports = proxy.GetPorts();

            // Assert
            Assert.Null(ports);
        }

        [Fact]
        public void GetPorts_WhenPodsReady_ReturnsPortsOfFirstReadyPod()
        {
            // Arrange
            var replicasService = new FakeReplicasService
            {
                Deployments = new FakeDeploymentCollection
                {
                    Functions = new List<DeploymentInformation>
                    {
                        new(
                            Deployment: "my-deployment",
                            Namespace: "default",
                            Pods: new List<PodInformation>
                            {
                                // Pod non prêt
                                new("pod1", true, false, "10.0.0.10", "my-deployment",
                                    new List<int> { 8001, 8002 }),

                                // Pod prêt
                                new("pod2", true, true, "10.0.0.11", "my-deployment",
                                    new List<int> { 9001, 9002 })
                            },
                            Configuration: new SlimFaasConfiguration(),
                            Replicas: 1
                        )
                    }
                }
            };

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            var ports = proxy.GetPorts();

            // Assert
            // On s'attend à récupérer les ports du premier pod "Ready"
            Assert.NotNull(ports);
            Assert.Equal(new List<int> { 9001, 9002 }, ports);
        }
    }

    // ===============================
    // 4) Fake service pour IReplicasService
    // ===============================
    public class FakeReplicasService : IReplicasService
    {
        private DeploymentsInformations _deployments;
        public FakeDeploymentCollection Deployments { get; set; } = new();
        public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();

        public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();

        DeploymentsInformations IReplicasService.Deployments =>
            new(Functions: Deployments.Functions, new SlimFaasDeploymentInformation(1, new List<PodInformation>()),new List<PodInformation>());
    }
}
