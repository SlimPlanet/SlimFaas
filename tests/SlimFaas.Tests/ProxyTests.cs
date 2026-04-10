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
            Proxy.ActiveRequestsPerPod.Clear();
            Proxy.LastRequestTicksPerPod.Clear();
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
        public void GetNextIP_LeastConnections_DistributesEvenly_TwoPodsWithTenRequests()
        {
            // Arrange: 2 pods, maxPerPod = 10, on envoie 10 requêtes
            // Chaque pod doit recevoir exactement 5 requêtes
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
                            Replicas: 2
                        )
                    }
                }
            };

            // Forcer un startIndex déterministe en fixant la dernière IP
            Proxy.IpAddresses["my-deployment"] = "10.0.0.2";

            var proxy = new Proxy(replicasService, "my-deployment");
            var distribution = new Dictionary<string, int>
            {
                ["10.0.0.1"] = 0,
                ["10.0.0.2"] = 0
            };

            // Act: simuler 10 requêtes avec IncrementActiveRequests
            for (int i = 0; i < 10; i++)
            {
                string ip = proxy.GetNextIP(10);
                Assert.NotEqual("", ip);
                proxy.IncrementActiveRequests(ip);
                distribution[ip]++;
            }

            // Assert: chaque pod reçoit exactement 5 requêtes
            Assert.Equal(5, distribution["10.0.0.1"]);
            Assert.Equal(5, distribution["10.0.0.2"]);
        }

        [Fact]
        public void GetNextIP_LeastConnections_PrefersLeastLoadedPod()
        {
            // Arrange: 3 pods, pod1 a déjà 5 requêtes actives, pod2 en a 2, pod3 en a 0
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
                            Replicas: 3
                        )
                    }
                }
            };

            // Simuler les requêtes actives existantes
            Proxy.ActiveRequestsPerPod["10.0.0.1"] = 5;
            Proxy.ActiveRequestsPerPod["10.0.0.2"] = 2;
            Proxy.ActiveRequestsPerPod["10.0.0.3"] = 0;

            // Forcer le startIndex pour que le round-robin commencerait par pod1
            Proxy.IpAddresses["my-deployment"] = "10.0.0.3";

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act: le pod avec le moins de requêtes actives devrait être choisi
            string ip = proxy.GetNextIP(10);

            // Assert: pod3 (0 requêtes actives) doit être choisi, pas pod1 (5 requêtes)
            Assert.Equal("10.0.0.3", ip);
        }

        [Fact]
        public void GetNextIP_LeastConnections_SkipsSaturatedPods()
        {
            // Arrange: 2 pods, pod1 est saturé (10/10), pod2 n'est pas saturé (3/10)
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
                            Replicas: 2
                        )
                    }
                }
            };

            Proxy.ActiveRequestsPerPod["10.0.0.1"] = 10;
            Proxy.ActiveRequestsPerPod["10.0.0.2"] = 3;

            // Le round-robin pointerait vers pod1
            Proxy.IpAddresses["my-deployment"] = "10.0.0.2";

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            string ip = proxy.GetNextIP(10);

            // Assert: pod1 est saturé, pod2 doit être choisi
            Assert.Equal("10.0.0.2", ip);
        }

        [Fact]
        public void GetNextIP_LeastConnections_AllSaturated_ReturnsEmpty()
        {
            // Arrange: 2 pods, les deux sont saturés
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
                            Replicas: 2
                        )
                    }
                }
            };

            Proxy.ActiveRequestsPerPod["10.0.0.1"] = 10;
            Proxy.ActiveRequestsPerPod["10.0.0.2"] = 10;

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            string ip = proxy.GetNextIP(10);

            // Assert
            Assert.Equal("", ip);
        }

        [Fact]
        public void GetNextIP_LeastConnections_TieBreaker_PrefersLeastRecentlyUsedPod()
        {
            // Arrange: 3 pods, mêmes requêtes actives; le tie-breaker doit choisir
            // le pod le moins récemment servi (tick le plus ancien).
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
                            Replicas: 3
                        )
                    }
                }
            };

            // Le round-robin partirait de pod2, mais pod3 est le moins récemment servi.
            Proxy.IpAddresses["my-deployment"] = "10.0.0.1";
            Proxy.LastRequestTicksPerPod["10.0.0.1"] = 300;
            Proxy.LastRequestTicksPerPod["10.0.0.2"] = 200;
            Proxy.LastRequestTicksPerPod["10.0.0.3"] = 100;

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            string ip = proxy.GetNextIP(10);

            // Assert: pod3 gagne car le moins récemment servi
            Assert.Equal("10.0.0.3", ip);
        }

        [Fact]
        public void GetNextIP_WhenActiveCountsStayZero_RotatesAcrossReplicas()
        {
            // Arrange: aucune incrémentation des requêtes actives entre les sélections.
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
                            Replicas: 3
                        )
                    }
                }
            };

            Proxy.IpAddresses["my-deployment"] = "10.0.0.3";
            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            var selected = new List<string>
            {
                proxy.GetNextIP(10),
                proxy.GetNextIP(10),
                proxy.GetNextIP(10)
            };

            // Assert: les 3 premières sélections couvrent tous les pods
            Assert.DoesNotContain("", selected);
            Assert.Equal(3, selected.Distinct().Count());
        }

        [Fact]
        public void GetNextIP_LeastConnections_ThreePodsWithSixRequests_DistributesEvenly()
        {
            // Arrange: 3 pods, maxPerPod = 10, on envoie 6 requêtes → chaque pod doit recevoir 2
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
                            Replicas: 3
                        )
                    }
                }
            };

            Proxy.IpAddresses["my-deployment"] = "10.0.0.3";

            var proxy = new Proxy(replicasService, "my-deployment");
            var distribution = new Dictionary<string, int>
            {
                ["10.0.0.1"] = 0,
                ["10.0.0.2"] = 0,
                ["10.0.0.3"] = 0
            };

            // Act
            for (int i = 0; i < 6; i++)
            {
                string ip = proxy.GetNextIP(10);
                Assert.NotEqual("", ip);
                proxy.IncrementActiveRequests(ip);
                distribution[ip]++;
            }

            // Assert: chaque pod reçoit exactement 2 requêtes
            Assert.Equal(2, distribution["10.0.0.1"]);
            Assert.Equal(2, distribution["10.0.0.2"]);
            Assert.Equal(2, distribution["10.0.0.3"]);
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
