using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using SlimFaas.Kubernetes;
using Xunit;

namespace SlimFaas.Tests.Kubernetes
{
    public class AutoScalerPeriodPoliciesMoreTests
    {
        private static PromQlMiniEvaluator CreateConstantEvaluator(double value)
        {
            // Snapshot non vide pour que Evaluate(...) ne retourne pas NaN.
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

        private static ScaleConfig CreateScaleConfigPodsUpWithPeriod(
            int podsPerScale,
            int periodSeconds)
        {
            return new ScaleConfig
            {
                ReplicaMax = 10,
                Triggers =
                {
                    new ScaleTrigger
                    {
                        MetricType = ScaleMetricType.Value,
                        MetricName = "avg_latency_fibonacci_seconds",
                        Query = "100",   // Evaluate("100") => 100.0
                        Threshold = 20   // ratio = 100 / 20 = 5
                    }
                },
                Behavior = new ScaleBehavior
                {
                    ScaleUp = new ScaleDirectionBehavior
                    {
                        StabilizationWindowSeconds = 0,
                        Policies = new List<ScalePolicy>
                        {
                            new ScalePolicy
                            {
                                Type = ScalePolicyType.Pods,
                                Value = podsPerScale,
                                PeriodSeconds = periodSeconds
                            }
                        }
                    },
                    ScaleDown = new ScaleDirectionBehavior
                    {
                        StabilizationWindowSeconds = 0,
                        Policies = new List<ScalePolicy>()
                    }
                }
            };
        }

        [Fact]
        public void ScaleUp_PodsPolicy_WithPeriodSeconds_LimitsDeltaWithinWindow()
        {
            // Arrange
            var evaluator = CreateConstantEvaluator(100);
            var samples = new List<AutoScaleSample>();

            var storeMock = new Mock<IAutoScalerStore>();

            storeMock
                .Setup(s => s.AddSample(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>()))
                .Callback<string, long, int>((key, ts, desired) =>
                {
                    samples.Add(new AutoScaleSample(ts, desired));
                });

            storeMock
                .Setup(s => s.GetSamples(It.IsAny<string>(), It.IsAny<long>()))
                .Returns<string, long>((key, fromTs) =>
                    samples
                        .Where(x => x.TimestampUnixSeconds >= fromTs)
                        .OrderBy(x => x.TimestampUnixSeconds)
                        .ToArray());

            var scaler = new AutoScaler(evaluator, storeMock.Object);

            var config = CreateScaleConfigPodsUpWithPeriod(
                podsPerScale: 1,
                periodSeconds: 30);

            var key = "slimfaas-demo/fibonacci1";
            var now = 1_000L;
            var currentReplicas = 1;

            // Act 1 : premier passage => on doit scaler de 1 -> 2
            var desired1 = scaler.ComputeDesiredReplicas(
                key,
                config,
                currentReplicas,
                minReplicas: 0,
                maxReplicas: 10,
                nowUnixSeconds: now);

            // Assert 1
            Assert.Equal(2, desired1);
            Assert.Single(samples);
            Assert.Equal(2, samples[0].DesiredReplicas);

            // Act 2 : 2 secondes plus tard, même forte charge
            currentReplicas = desired1;
            now += 2;

            var desired2 = scaler.ComputeDesiredReplicas(
                key,
                config,
                currentReplicas,
                minReplicas: 0,
                maxReplicas: 10,
                nowUnixSeconds: now);

            // Assert 2 : aucun nouveau scale-up autorisé dans la même fenêtre de 30s
            Assert.Equal(2, desired2);
            Assert.Single(samples); // pas de nouveau sample ajouté
        }

        [Fact]
        public void ScaleUp_PodsPolicy_AfterPeriodSeconds_AllowsNewIncrease()
        {
            // Arrange
            var evaluator = CreateConstantEvaluator(100);
            var samples = new List<AutoScaleSample>();

            var storeMock = new Mock<IAutoScalerStore>();

            storeMock
                .Setup(s => s.AddSample(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>()))
                .Callback<string, long, int>((key, ts, desired) =>
                {
                    samples.Add(new AutoScaleSample(ts, desired));
                });

            storeMock
                .Setup(s => s.GetSamples(It.IsAny<string>(), It.IsAny<long>()))
                .Returns<string, long>((key, fromTs) =>
                    samples
                        .Where(x => x.TimestampUnixSeconds >= fromTs)
                        .OrderBy(x => x.TimestampUnixSeconds)
                        .ToArray());

            var scaler = new AutoScaler(evaluator, storeMock.Object);

            var config = CreateScaleConfigPodsUpWithPeriod(
                podsPerScale: 1,
                periodSeconds: 30);

            var key = "slimfaas-demo/fibonacci1";
            var now = 1_000L;
            var currentReplicas = 1;

            // Act 1 : premier scale-up
            var desired1 = scaler.ComputeDesiredReplicas(
                key,
                config,
                currentReplicas,
                minReplicas: 0,
                maxReplicas: 10,
                nowUnixSeconds: now);
            Assert.Equal(2, desired1);
            currentReplicas = desired1;

            // On avance de 31 secondes (au-delà des 30s de PeriodSeconds)
            now += 31;

            var desired2 = scaler.ComputeDesiredReplicas(
                key,
                config,
                currentReplicas,
                minReplicas: 0,
                maxReplicas: 10,
                nowUnixSeconds: now);

            // Assert : on peut à nouveau augmenter (2 -> 3)
            Assert.Equal(3, desired2);
            Assert.True(samples.Count >= 2);
        }
    }
}
