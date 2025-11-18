using System;
using System.Collections.Generic;
using Moq;
using SlimFaas.Kubernetes;
using Xunit;

namespace SlimFaas.Tests.Kubernetes
{
    public class AutoScalerNoTrafficTests
    {
        /// <summary>
        /// Crée un evaluator dont le snapshot est non vide, mais sans métriques.
        /// La requête "0" sera évaluée comme un scalaire constant 0, sans toucher au snapshot.
        /// </summary>
        private static PromQlMiniEvaluator CreateEvaluatorWithDummySnapshot()
        {
            return new PromQlMiniEvaluator(() =>
                new Dictionary<long,
                    IReadOnlyDictionary<string,
                        IReadOnlyDictionary<string,
                            IReadOnlyDictionary<string, double>>>>
                {
                    {
                        1_000L,
                        new Dictionary<string,
                            IReadOnlyDictionary<string,
                                IReadOnlyDictionary<string, double>>>()
                    }
                });
        }

        private static ScaleConfig CreateScaleConfigForPodsScaleDown(
            int stabilizationWindowSeconds,
            int maxPodsChangePerStep)
        {
            return new ScaleConfig
            {
                Triggers =
                {
                    new ScaleTrigger
                    {
                        // On force metricValue = 0 en utilisant une constante.
                        // Evaluate("0", ...) => 0.0
                        Query = "0",
                        Threshold = 1.0  // ratio = 0 / 1 => rawDesired = 0
                    }
                },
                Behavior = new ScaleBehavior
                {
                    ScaleUp = new ScaleDirectionBehavior
                    {
                        StabilizationWindowSeconds = 0,
                        Policies = new List<ScalePolicy>()
                    },
                    ScaleDown = new ScaleDirectionBehavior
                    {
                        StabilizationWindowSeconds = stabilizationWindowSeconds,
                        Policies = new List<ScalePolicy>
                        {
                            new ScalePolicy
                            {
                                Type = ScalePolicyType.Pods,
                                Value = maxPodsChangePerStep,
                                PeriodSeconds = 0
                            }
                        }
                    }
                }
            };
        }

        [Fact]
        public void ComputeDesiredReplicas_NoTraffic_ShouldStoreRawDesiredZero_AndScaleDownByOne()
        {
            // Arrange
            var evaluator = CreateEvaluatorWithDummySnapshot();

            var storeMock = new Mock<IAutoScalerStore>();

            // Pas d'historique pour ce test : aucune stabilisation ne s'applique.
            storeMock
                .Setup(s => s.GetSamples(It.IsAny<string>(), It.IsAny<long>()))
                .Returns(Array.Empty<AutoScaleSample>());

            var scaler = new AutoScaler(evaluator, storeMock.Object);

            var config = CreateScaleConfigForPodsScaleDown(
                stabilizationWindowSeconds: 0,
                maxPodsChangePerStep: 1);

            var now = 1_000L;
            var currentReplicas = 9;

            // Act
            var desired = scaler.ComputeDesiredReplicas(
                key: "ns/app",
                scaleConfig: config,
                currentReplicas: currentReplicas,
                minReplicas: 0,
                maxReplicas: null,
                nowUnixSeconds: now);

            // Assert : on scale down d'un seul pod (9 -> 8)
            Assert.Equal(8, desired);

            // Et on vérifie que ce qui est stocké dans le store est bien le rawDesired = 0,
            // pas la valeur finale 8.
            storeMock.Verify(
                s => s.AddSample("ns/app", now, 8),
                Times.Once);
        }

        [Fact]
        public void ComputeDesiredReplicas_NoTraffic_MultipleIterations_ShouldDecreaseUntilZero()
        {
            // Arrange
            var evaluator = CreateEvaluatorWithDummySnapshot();

            var storeMock = new Mock<IAutoScalerStore>();

            // Pour ce test, on ignore complètement l'historique :
            // GetSamples retourne toujours une liste vide.
            storeMock
                .Setup(s => s.GetSamples(It.IsAny<string>(), It.IsAny<long>()))
                .Returns(Array.Empty<AutoScaleSample>());

            var scaler = new AutoScaler(evaluator, storeMock.Object);

            var config = CreateScaleConfigForPodsScaleDown(
                stabilizationWindowSeconds: 0,   // pas de stabilisation
                maxPodsChangePerStep: 1);        // on descend d'un pod par itération

            var now = 1_000L;
            var currentReplicas = 3;

            // Act : on simule plusieurs boucles de réconciliation
            var desired1 = scaler.ComputeDesiredReplicas(
                "ns/app", config, currentReplicas,
                minReplicas: 0, maxReplicas: null, nowUnixSeconds: now);
            currentReplicas = desired1;
            now += 30;

            var desired2 = scaler.ComputeDesiredReplicas(
                "ns/app", config, currentReplicas,
                minReplicas: 0, maxReplicas: null, nowUnixSeconds: now);
            currentReplicas = desired2;
            now += 30;

            var desired3 = scaler.ComputeDesiredReplicas(
                "ns/app", config, currentReplicas,
                minReplicas: 0, maxReplicas: null, nowUnixSeconds: now);

            // Assert : 3 -> 2 -> 1 -> 0
            Assert.Equal(2, desired1);
            Assert.Equal(1, desired2);
            Assert.Equal(0, desired3);

            // Et on vérifie accessoirement qu'on a bien stocké 3 rawDesired = 0
            storeMock.Verify(
                s => s.AddSample("ns/app", It.IsAny<long>(), 0),
                Times.Exactly(1));
        }
    }
}
