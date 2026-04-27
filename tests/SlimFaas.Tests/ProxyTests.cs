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

    public class ProxyTests
    {
        /// <summary>
        /// Nettoyage du dictionnaire statique avant chaque test (round-robin hint).
        /// </summary>
        public ProxyTests()
        {
            Proxy.IpAddresses.Clear();
        }

        private static readonly IReadOnlyCollection<string> NoUsage = Array.Empty<string>();

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
            // Assert
            Assert.Equal("", proxy.GetNextIP());
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
            // Assert
            Assert.Equal("", proxy.GetNextIP());
        }

        [Fact]
        public void GetNextIP_WhenNoLastIpExists_ReturnsRandomPod()
        {
            // Arrange
            var replicasService = BuildThreePods();
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
            var replicasService = BuildThreePods();
            Proxy.IpAddresses["my-deployment"] = "10.0.0.1";

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            // Assert
            Assert.Equal("10.0.0.2", proxy.GetNextIP());
            Assert.Equal("10.0.0.3", proxy.GetNextIP());
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
            // Assert
            Assert.Contains(proxy.GetNextIP(), new[] { "10.0.0.1", "10.0.0.2" });
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

            // L'appelant maintient lui-même la liste des IPs en cours d'utilisation
            // (équivalent du contenu "Running" en DB).
            var inUse = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                string ip = proxy.GetNextIP(10, inUse);
                Assert.NotEqual("", ip);
                inUse.Add(ip);
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
            var replicasService = BuildThreePods();

            // Charge actuelle : pod1=5, pod2=2, pod3=0
            var alreadyUsed = new List<string>
            {
                "10.0.0.1","10.0.0.1","10.0.0.1","10.0.0.1","10.0.0.1",
                "10.0.0.2","10.0.0.2"
            };

            Proxy.IpAddresses["my-deployment"] = "10.0.0.3"; // le round-robin commencerait par pod1
            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            string ip = proxy.GetNextIP(10, alreadyUsed);

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

            var alreadyUsed = new List<string>();
            for (int i = 0; i < 10; i++) alreadyUsed.Add("10.0.0.1"); // pod1 saturé
            for (int i = 0; i < 3; i++) alreadyUsed.Add("10.0.0.2");

            Proxy.IpAddresses["my-deployment"] = "10.0.0.2";
            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            // Assert: pod1 est saturé, pod2 doit être choisi
            Assert.Equal("10.0.0.2", proxy.GetNextIP(10, alreadyUsed));
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

            var alreadyUsed = new List<string>();
            for (int i = 0; i < 10; i++) alreadyUsed.Add("10.0.0.1");
            for (int i = 0; i < 10; i++) alreadyUsed.Add("10.0.0.2");

            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            // Assert
            Assert.Equal("", proxy.GetNextIP(10, alreadyUsed));
        }

        [Fact]
        public void GetNextIP_WhenActiveCountsStayZero_RotatesAcrossReplicas()
        {
            // Arrange: aucune incrémentation des requêtes actives entre les sélections.
            var replicasService = BuildThreePods();
            Proxy.IpAddresses["my-deployment"] = "10.0.0.3";
            var proxy = new Proxy(replicasService, "my-deployment");

            // Act
            var selected = new List<string>
            {
                proxy.GetNextIP(10, NoUsage),
                proxy.GetNextIP(10, NoUsage),
                proxy.GetNextIP(10, NoUsage)
            };

            // Assert: les 3 premières sélections couvrent tous les pods
            Assert.DoesNotContain("", selected);
            Assert.Equal(3, selected.Distinct().Count());
        }

        [Fact]
        public void GetNextIP_LeastConnections_ThreePodsWithSixRequests_DistributesEvenly()
        {
            // Arrange: 3 pods, maxPerPod = 10, on envoie 6 requêtes → chaque pod doit recevoir 2
            var replicasService = BuildThreePods();
            Proxy.IpAddresses["my-deployment"] = "10.0.0.3";

            var proxy = new Proxy(replicasService, "my-deployment");
            var distribution = new Dictionary<string, int>
            {
                ["10.0.0.1"] = 0,
                ["10.0.0.2"] = 0,
                ["10.0.0.3"] = 0
            };

            var inUse = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                string ip = proxy.GetNextIP(10, inUse);
                Assert.NotEqual("", ip);
                inUse.Add(ip);
                distribution[ip]++;
            }

            // Assert: chaque pod reçoit exactement 2 requêtes
            Assert.Equal(2, distribution["10.0.0.1"]);
            Assert.Equal(2, distribution["10.0.0.2"]);
            Assert.Equal(2, distribution["10.0.0.3"]);
        }

        [Fact]
        public void ReserveNextIPs_AppendsToAlreadyUsedAndRespectsMaxPerPod()
        {
            // Arrange
            var replicasService = BuildThreePods();
            Proxy.IpAddresses["my-deployment"] = "10.0.0.3";
            var proxy = new Proxy(replicasService, "my-deployment");

            // 2 pods quasiment pleins (maxPerPod = 3) ; un seul slot disponible sur pod1
            // et un seul sur pod2 ; 3 slots dispo sur pod3
            var alreadyUsed = new List<string>
            {
                "10.0.0.1","10.0.0.1",
                "10.0.0.2","10.0.0.2",
            };

            // Act
            var reserved = proxy.ReserveNextIPs(maxPerPod: 3, count: 5, alreadyUsedIps: alreadyUsed);

            // Assert
            // 1 (pod1) + 1 (pod2) + 3 (pod3) = 5
            Assert.Equal(5, reserved.Count);
            Assert.Equal(1, reserved.Count(x => x == "10.0.0.1"));
            Assert.Equal(1, reserved.Count(x => x == "10.0.0.2"));
            Assert.Equal(3, reserved.Count(x => x == "10.0.0.3"));
        }

        [Fact]
        public void AcquireNextIPForSync_DistributesInFlightReservationsByLeastConnections()
        {
            // Arrange
            var replicasService = BuildThreePods();
            Proxy.IpAddresses["my-deployment"] = "10.0.0.3";
            var proxy = new Proxy(replicasService, "my-deployment");
            var reserved = new List<string>();

            try
            {
                // Act
                for (int i = 0; i < 6; i++)
                {
                    reserved.Add(proxy.AcquireNextIPForSync(maxPerPod: 10));
                }

                // Assert
                Assert.DoesNotContain("", reserved);
                Assert.Equal(2, reserved.Count(x => x == "10.0.0.1"));
                Assert.Equal(2, reserved.Count(x => x == "10.0.0.2"));
                Assert.Equal(2, reserved.Count(x => x == "10.0.0.3"));
            }
            finally
            {
                foreach (var ip in reserved)
                {
                    proxy.ReleaseSyncIP(ip);
                }
            }
        }

        [Fact]
        public void AcquireNextIPForSync_RespectsMaxPerPodAndReusesReleasedSlots()
        {
            // Arrange
            var replicasService = BuildThreePods();
            Proxy.IpAddresses["my-deployment"] = "10.0.0.3";
            var proxy = new Proxy(replicasService, "my-deployment");
            var reserved = new List<string>();

            try
            {
                // Act
                reserved.Add(proxy.AcquireNextIPForSync(maxPerPod: 1));
                reserved.Add(proxy.AcquireNextIPForSync(maxPerPod: 1));
                reserved.Add(proxy.AcquireNextIPForSync(maxPerPod: 1));

                // Assert: les trois pods sont saturés à 1 requête chacun.
                Assert.Equal(3, reserved.Distinct().Count());
                Assert.Equal("", proxy.AcquireNextIPForSync(maxPerPod: 1));

                proxy.ReleaseSyncIP(reserved[0]);
                Assert.Equal(reserved[0], proxy.AcquireNextIPForSync(maxPerPod: 1));
            }
            finally
            {
                foreach (var ip in reserved)
                {
                    proxy.ReleaseSyncIP(ip);
                }
            }
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

        private static FakeReplicasService BuildThreePods()
        {
            return new FakeReplicasService
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
        }
    }

    public class FakeReplicasService : IReplicasService
    {
        public FakeDeploymentCollection Deployments { get; set; } = new();
        public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();

        public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();

        DeploymentsInformations IReplicasService.Deployments =>
            new(Functions: Deployments.Functions, new SlimFaasDeploymentInformation(1, new List<PodInformation>()), new List<PodInformation>());
    }
}
